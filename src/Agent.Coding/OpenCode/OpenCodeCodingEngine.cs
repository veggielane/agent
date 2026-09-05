using System.Diagnostics;
using System.Text;
using Agent.Coding.Sandbox;
using Agent.Core.Llm;
using Agent.Core.Observability;
using Agent.Core.Pipeline;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Coding.OpenCode;

/// <summary>
/// Runs the task with the opencode CLI instead of the built-in loop. opencode owns the agent loop, so the
/// orchestrator keeps only what it can still guarantee: the sandbox, a wall-clock budget, generated
/// permission rules, verification, and a post-run check that no protected path was touched. Git, branching
/// and publishing stay on this side, exactly as with the native engine.
/// </summary>
public sealed class OpenCodeCodingEngine : ICodingEngine
{
    private readonly ISandbox _sandbox;
    private readonly IGitRunner _git;
    private readonly IProcessRunner _processes;
    private readonly IOptionsMonitor<CodingOptions> _options;
    private readonly IOptionsMonitor<LlmOptions> _llm;
    private readonly IChatClientFactory _clients;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OpenCodeCodingEngine> _logger;

    public OpenCodeCodingEngine(
        ISandbox sandbox,
        IGitRunner git,
        IProcessRunner processes,
        IOptionsMonitor<CodingOptions> options,
        IOptionsMonitor<LlmOptions> llm,
        IChatClientFactory clients,
        ILoggerFactory loggerFactory)
    {
        _sandbox = sandbox;
        _git = git;
        _processes = processes;
        _options = options;
        _llm = llm;
        _clients = clients;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OpenCodeCodingEngine>();
    }

    public async Task<CodingResult> RunAsync(CodingRun run, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var openCode = options.OpenCode;
        var llm = _llm.CurrentValue;
        var workspace = run.Workspace;
        var model = string.IsNullOrWhiteSpace(openCode.Model) ? _clients.ResolveModel(ModelPurpose.Coding) : openCode.Model.Trim();

        using var activity = AgentTelemetry.Source.StartActivity("agent.coding.run", ActivityKind.Internal);
        activity
            .Tag("agent.task.id", workspace.TaskId)
            .Tag("agent.coding.engine", "opencode")
            .Tag("agent.branch", workspace.Branch)
            .Tag("gen_ai.request.model", model);

        using var requestScope = RequestContext.Begin(run.Requester);

        ISandboxSession session;
        try
        {
            progress?.Report($"starting {_sandbox.Name} sandbox");
            session = await _sandbox.StartAsync(workspace, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Stopped(CodingStopReason.Cancelled, "Cancelled before the sandbox started.", null);
        }
        catch (Exception ex)
        {
            activity.Failed(ex);
            return Stopped(CodingStopReason.Error, $"The {_sandbox.Name} sandbox could not be started.", ex.Message);
        }

        await using var sandboxSession = session.ConfigureAwait(false);
        activity.Tag("agent.sandbox.mode", session.Mode);

        if (session.Mode == "process")
        {
            // opencode owns its own tool loop, so the generated permission rules are enforced by opencode
            // rather than by us. On the host that is the only barrier; in a container it is the second one.
            _logger.LogWarning(
                "Task #{Task} runs opencode directly on the worker. Set Coding:Sandbox:Mode to Docker so an external agent is contained.",
                workspace.TaskId);
        }

        var state = new CodingRunState();
        var toolset = new CodingToolset(workspace, options, _processes, _git, state, _loggerFactory.CreateLogger<CodingToolset>(), session);
        var stopwatch = Stopwatch.StartNew();
        var budget = TimeSpan.FromMinutes(Math.Max(1, options.Budget.MaxMinutes));

        try
        {
            var preflight = await session.ExecuteAsync(
                new ParsedCommand(openCode.ExecutablePath, ["--version"]),
                TimeSpan.FromSeconds(Math.Max(5, openCode.PreflightTimeoutSeconds)),
                cancellationToken).ConfigureAwait(false);

            if (!preflight.Success)
            {
                return Stopped(
                    CodingStopReason.Error,
                    "The opencode CLI is not available where commands run.",
                    session.Mode == "docker"
                        ? $"'{openCode.ExecutablePath} --version' failed inside the sandbox image. Add opencode to the image, or set Coding:Engine to Native. Output: {Short(preflight.CombinedOutput)}"
                        : $"'{openCode.ExecutablePath} --version' failed on the worker. Install opencode, or set Coding:Engine to Native. Output: {Short(preflight.CombinedOutput)}");
            }

            activity.Tag("agent.coding.engine.version", preflight.StdOut.Trim());

            WriteConfig(workspace, openCode, options, llm, model);
            ExcludeFromGit(workspace, openCode.ConfigFileName);

            var environment = BuildEnvironment(openCode, llm, session);
            var arguments = BuildRunArguments(openCode, model, BuildMessage(run), continueSession: false);

            progress?.Report($"opencode run ({model})");
            var result = await session.ExecuteAsync(new ParsedCommand(openCode.ExecutablePath, arguments), budget, cancellationToken, environment).ConfigureAwait(false);
            var output = OpenCodeOutput.Parse(result.StdOut);
            var turns = 1;

            var stop = result.TimedOut ? CodingStopReason.BudgetTime
                : result.Success ? CodingStopReason.Done
                : CodingStopReason.Error;
            var error = result.Success || result.TimedOut ? null : Short(result.CombinedOutput);

            if (stop == CodingStopReason.Error)
            {
                _logger.LogWarning("opencode exited with {ExitCode} for task #{Task}", result.ExitCode, workspace.TaskId);
            }

            // Verification runs on our side, and a failure is fed back through --continue.
            bool? verified = null;
            string? verificationOutput = null;
            if (stop is not CodingStopReason.Cancelled && workspace.Profile.HasVerification)
            {
                var retries = 0;
                while (true)
                {
                    progress?.Report("verifying");
                    var (ok, verifyOutput) = await VerifyAsync(toolset, workspace.Profile, cancellationToken).ConfigureAwait(false);
                    verificationOutput = verifyOutput;
                    if (ok)
                    {
                        verified = true;
                        break;
                    }

                    if (stop != CodingStopReason.Done || retries >= options.Budget.MaxVerifyRetries || stopwatch.Elapsed >= budget || cancellationToken.IsCancellationRequested)
                    {
                        verified = false;
                        break;
                    }

                    retries++;
                    progress?.Report($"verification failed; opencode fix-up {retries}/{options.Budget.MaxVerifyRetries}");
                    var fixUp = BuildRunArguments(
                        openCode,
                        model,
                        "The build or tests failed after your changes:\n\n" + TextUtil.TruncateMiddle(verifyOutput, options.MaxToolOutputChars) + "\n\nFix the problem. Do not commit or push.",
                        continueSession: true);

                    var fix = await session.ExecuteAsync(new ParsedCommand(openCode.ExecutablePath, fixUp), Remaining(budget, stopwatch), cancellationToken, environment).ConfigureAwait(false);
                    turns++;
                    var fixOutput = OpenCodeOutput.Parse(fix.StdOut);
                    output = output with
                    {
                        Summary = string.IsNullOrWhiteSpace(fixOutput.Summary) ? output.Summary : output.Summary + "\n\n" + fixOutput.Summary,
                        InputTokens = output.InputTokens + fixOutput.InputTokens,
                        OutputTokens = output.OutputTokens + fixOutput.OutputTokens,
                    };

                    if (fix.TimedOut)
                    {
                        stop = CodingStopReason.BudgetTime;
                        verified = false;
                        break;
                    }
                }
            }

            var changedFiles = await ChangedFilesAsync(workspace, cancellationToken).ConfigureAwait(false);

            // opencode's permission rules should have prevented this; checking is what makes it a guarantee.
            var violation = FindProtectedViolation(toolset, changedFiles);
            if (violation is not null)
            {
                _logger.LogError("opencode modified protected path {Path} for task #{Task}", violation, workspace.TaskId);
                return new CodingResult(
                    $"Stopped: opencode modified a protected path ({violation}).",
                    changedFiles,
                    state.CommandsRun.ToList(),
                    false,
                    verificationOutput,
                    turns,
                    output.TotalTokens,
                    CodingStopReason.Error,
                    $"Protected path '{violation}' was modified. The change was not published.");
            }

            AgentTelemetry.RecordTokens(
                output.TotalTokens > 0 ? new Microsoft.Extensions.AI.UsageDetails { InputTokenCount = output.InputTokens, OutputTokenCount = output.OutputTokens } : null,
                nameof(ModelPurpose.Coding),
                "Coding",
                model);

            activity
                .Tag("agent.stop_reason", stop.ToString())
                .Tag("agent.turns", turns)
                .Tag("agent.tokens", output.TotalTokens)
                .Tag("agent.verified", verified)
                .Tag("agent.changed_files", changedFiles.Count);

            var summary = string.IsNullOrWhiteSpace(output.Summary)
                ? stop == CodingStopReason.Done ? "(opencode reported no summary)" : $"Stopped: {stop}"
                : TextUtil.TruncateEnd(output.Summary, 8000);

            _logger.LogInformation(
                "opencode run for task #{Task} stopped with {Stop} after {Elapsed:0}s; changed {Files} files; verified={Verified}",
                workspace.TaskId,
                stop,
                stopwatch.Elapsed.TotalSeconds,
                changedFiles.Count,
                verified);

            return new CodingResult(summary, changedFiles, state.CommandsRun.ToList(), verified, verificationOutput, turns, output.TotalTokens, stop, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Stopped(CodingStopReason.Cancelled, "Cancelled before completion.", null);
        }
        catch (Exception ex)
        {
            activity.Failed(ex);
            _logger.LogError(ex, "opencode run failed for task #{Task}", workspace.TaskId);
            return Stopped(CodingStopReason.Error, "The opencode run failed.", ex.Message);
        }
        finally
        {
            DeleteConfig(workspace, openCode.ConfigFileName);
        }
    }

    public static IReadOnlyList<string> BuildRunArguments(OpenCodeOptions options, string model, string message, bool continueSession)
    {
        var args = new List<string> { "run", "--format", "json", "--auto", "--model", $"{options.ProviderId}/{model}" };

        if (continueSession)
        {
            args.Add("--continue");
        }

        if (!string.IsNullOrWhiteSpace(options.Agent))
        {
            args.Add("--agent");
            args.Add(options.Agent.Trim());
        }

        args.AddRange(options.ExtraArgs.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()));
        args.Add(message);
        return args;
    }

    public static string BuildMessage(CodingRun run)
    {
        var sb = new StringBuilder();
        if (run.IsFollowUp)
        {
            sb.Append("Follow-up on the branch that is already checked out.\n\n");
            if (!string.IsNullOrWhiteSpace(run.PreviousSummary))
            {
                sb.Append("What was done previously:\n").Append(TextUtil.TruncateEnd(run.PreviousSummary, 4000)).Append("\n\n");
            }
        }

        sb.Append(run.Instruction.Trim());
        sb.Append("\n\nDo not commit, push, or switch branches: that is handled after you finish. ");
        sb.Append("Finish with a short summary of what you changed and what you verified.");
        return sb.ToString();
    }

    private Dictionary<string, string> BuildEnvironment(OpenCodeOptions openCode, LlmOptions llm, ISandboxSession session)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENCODE_CONFIG"] = session.PathInSandbox(openCode.ConfigFileName),
        };

        // opencode has to call the model, so the key necessarily enters the sandbox. Passing it per command
        // keeps it out of the workspace; on the container path it is visible in the docker exec arguments.
        if (!string.IsNullOrWhiteSpace(llm.ApiKey) && !string.IsNullOrWhiteSpace(openCode.ApiKeyEnvironmentVariable))
        {
            environment[openCode.ApiKeyEnvironmentVariable] = llm.ApiKey;
        }

        return environment;
    }

    private void WriteConfig(Workspace workspace, OpenCodeOptions openCode, CodingOptions coding, LlmOptions llm, string model)
    {
        var json = OpenCodeConfigWriter.Build(openCode, coding, llm, workspace.Profile, model);
        var path = Path.Combine(workspace.RepoPath, openCode.ConfigFileName);
        File.WriteAllText(path, json);
        _logger.LogDebug("Wrote opencode config for task #{Task} to {Path}", workspace.TaskId, path);
    }

    /// <summary>Keeps the generated config out of <c>git status</c>, so it can never reach a commit.</summary>
    private void ExcludeFromGit(Workspace workspace, string fileName)
    {
        try
        {
            var excludePath = Path.Combine(workspace.RepoPath, ".git", "info", "exclude");
            Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
            var existing = File.Exists(excludePath) ? File.ReadAllText(excludePath) : string.Empty;
            if (!existing.Contains(fileName, StringComparison.Ordinal))
            {
                File.AppendAllText(excludePath, $"{Environment.NewLine}/{fileName}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not exclude {File} from git for task #{Task}", fileName, workspace.TaskId);
        }
    }

    private void DeleteConfig(Workspace workspace, string fileName)
    {
        try
        {
            var path = Path.Combine(workspace.RepoPath, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the generated opencode config for task #{Task}", workspace.TaskId);
        }
    }

    internal static string? FindProtectedViolation(CodingToolset toolset, IReadOnlyList<string> changedFiles)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var pattern in toolset.Paths.ProtectedPatterns)
        {
            matcher.AddInclude(pattern);
        }

        return changedFiles.FirstOrDefault(file => matcher.Match(file.Replace('\\', '/')).HasMatches);
    }

    private static TimeSpan Remaining(TimeSpan budget, Stopwatch elapsed)
    {
        var left = budget - elapsed.Elapsed;
        return left < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : left;
    }

    private static async Task<(bool Ok, string Output)> VerifyAsync(CodingToolset toolset, RepoProfile profile, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        foreach (var command in new[] { profile.BuildCommand, profile.TestCommand })
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            sb.Append("$ ").Append(command).Append('\n');
            var (ok, output) = await toolset.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            sb.Append(output.TrimEnd()).Append('\n');
            if (!ok)
            {
                return (false, sb.ToString());
            }
        }

        return (true, sb.ToString());
    }

    private async Task<IReadOnlyList<string>> ChangedFilesAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            var status = await _git.StatusAsync(workspace.RepoPath, CancellationToken.None).ConfigureAwait(false);
            return GitRunner.ParseStatus(status);
        }
        catch (Exception ex) when (ex is GitException or IOException)
        {
            _logger.LogWarning(ex, "Could not read git status for task #{Task}", workspace.TaskId);
            return [];
        }
    }

    private static CodingResult Stopped(CodingStopReason reason, string summary, string? error)
        => new(summary, [], [], null, null, 0, 0, reason, error);

    private static string Short(string text) => TextUtil.TruncateEnd(text.Trim(), 2000);
}
