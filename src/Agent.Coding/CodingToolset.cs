using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Agent.Coding.Sandbox;
using Agent.Core.Observability;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;

namespace Agent.Coding;

/// <summary>Mutable per-run state shared between the tools and the engine loop.</summary>
public sealed class CodingRunState
{
    public bool Completed { get; set; }

    public string? Summary { get; set; }

    public int RunCount { get; set; }

    public List<string> CommandsRun { get; } = [];
}

/// <summary>
/// The file, search, edit, run, and git tools offered to the model. One instance per coding run.
/// Every path goes through <see cref="PathGuard"/>, every command through <see cref="CommandPolicy"/>,
/// and nothing here throws to the model: failures come back as <c>error: ...</c> text.
/// </summary>
public sealed class CodingToolset
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "__pycache__", ".venv", "venv", ".mypy_cache", ".pytest_cache",
    };

    private const int MaxSearchFileBytes = 2 * 1024 * 1024;
    private const int BinaryProbeBytes = 8000;

    private readonly Workspace _workspace;
    private readonly CodingOptions _options;
    private readonly IGitRunner _git;
    private readonly CodingRunState _state;
    private readonly ILogger _logger;
    private readonly IProgress<CodingAction>? _actions;

    /// <param name="sandbox">
    /// Where <c>run</c> and verification commands execute. Omit to run them as host processes
    /// (<see cref="ProcessSandboxSession"/>), which is what the process sandbox mode does.
    /// </param>
    /// <param name="actions">Told about every file written and command run, for the task's event log.</param>
    public CodingToolset(
        Workspace workspace,
        CodingOptions options,
        IProcessRunner processes,
        IGitRunner git,
        CodingRunState state,
        ILogger logger,
        ISandboxSession? sandbox = null,
        IProgress<CodingAction>? actions = null)
    {
        _workspace = workspace;
        _options = options;
        _git = git;
        _state = state;
        _logger = logger;
        _actions = actions;
        Sandbox = sandbox ?? new ProcessSandboxSession(processes, workspace);

        Paths = new PathGuard(workspace.RepoPath, options.ProtectedPaths.Concat(workspace.Profile.ExtraProtectedPaths));
        Commands = new CommandPolicy(options.AllowedExecutables.Concat(workspace.Profile.ExtraAllowedExecutables), options.AllowedGitSubcommands);
    }

    public PathGuard Paths { get; }

    public CommandPolicy Commands { get; }

    /// <summary>The environment <c>run</c> and verification commands execute in.</summary>
    public ISandboxSession Sandbox { get; }

    public IReadOnlyList<AIFunction> CreateTools() =>
    [
        AIFunctionFactory.Create(ListFiles, "list_files", "Lists files in the repository (relative paths, forward slashes). Optional glob such as src/**/*.cs."),
        AIFunctionFactory.Create(ReadFile, "read_file", "Reads a text file with line numbers. Use startLine/endLine (1-based, inclusive) for large files."),
        AIFunctionFactory.Create(Search, "search", "Searches file contents with a .NET regular expression and returns path:line: text matches."),
        AIFunctionFactory.Create(WriteFile, "write_file", "Creates or overwrites a text file with the given content. Parent directories are created."),
        AIFunctionFactory.Create(EditFile, "edit_file", "Replaces one exact occurrence of oldText with newText in a file. oldText must match exactly once; include enough context."),
        AIFunctionFactory.Create(RunAsync, "run", "Runs an allow-listed executable (e.g. dotnet, npm, git status) in the repository root. No shell: one executable per call, no pipes or redirects."),
        AIFunctionFactory.Create(GitDiffAsync, "git_diff", "Shows the current diff of the working tree against HEAD plus the list of untracked files."),
        AIFunctionFactory.Create(GitStatusAsync, "git_status", "Shows git status in porcelain format."),
        AIFunctionFactory.Create(Done, "done", "Call this once the task is complete (or cannot be completed) with a short summary of what changed and what was verified. Ends the session."),
    ];

    public string ListFiles(
        [Description("Glob relative to the repository root, e.g. src/**/*.cs. Default: every file.")] string? glob = null,
        [Description("Maximum number of entries to return.")] int max = 200)
    {
        try
        {
            max = Math.Clamp(max, 1, 2000);
            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(string.IsNullOrWhiteSpace(glob) ? "**/*" : glob.Trim().Replace('\\', '/'));

            var results = new List<string>();
            var total = 0;
            foreach (var rel in EnumerateFiles())
            {
                if (!matcher.Match(rel).HasMatches)
                {
                    continue;
                }

                total++;
                if (results.Count < max)
                {
                    results.Add(rel);
                }
            }

            if (results.Count == 0)
            {
                return "(no files match)";
            }

            var sb = new StringBuilder();
            foreach (var r in results)
            {
                sb.Append(r).Append('\n');
            }

            if (total > results.Count)
            {
                sb.Append($"…({total - results.Count} more; narrow the glob or raise max)\n");
            }

            return Bound(sb.ToString());
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    public string ReadFile(
        [Description("Path relative to the repository root.")] string path,
        [Description("First line to return (1-based).")] int? startLine = null,
        [Description("Last line to return (1-based, inclusive).")] int? endLine = null)
    {
        try
        {
            var full = Paths.Resolve(path);
            if (Directory.Exists(full))
            {
                return $"error: '{path}' is a directory; use list_files.";
            }

            if (!File.Exists(full))
            {
                return $"error: '{path}' does not exist.";
            }

            if (IsBinary(full))
            {
                return $"error: '{path}' is a binary file.";
            }

            var lines = TextUtil.NewlineRegex().Split(File.ReadAllText(full));
            if (lines.Length > 0 && lines[^1].Length == 0)
            {
                lines = lines[..^1];
            }

            var start = Math.Max(1, startLine ?? 1);
            var end = Math.Min(lines.Length, endLine ?? lines.Length);
            if (lines.Length == 0)
            {
                return "(empty file)";
            }

            if (start > end)
            {
                return $"error: line range {start}-{end} is outside the file ({lines.Length} lines).";
            }

            var sb = new StringBuilder();
            for (var i = start; i <= end; i++)
            {
                sb.Append(i).Append(": ").Append(lines[i - 1]).Append('\n');
            }

            var text = sb.ToString();
            if (text.Length > _options.MaxFileReadChars)
            {
                text = TextUtil.TruncateEnd(text, _options.MaxFileReadChars) + "\n(use startLine/endLine to read the rest)";
            }

            return text;
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    public string Search(
        [Description("Regular expression (.NET syntax), matched per line.")] string pattern,
        [Description("Restrict to files matching this glob, e.g. **/*.cs.")] string? glob = null,
        [Description("Maximum number of matches.")] int max = 50)
    {
        try
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return "error: pattern is required.";
            }

            max = Math.Clamp(max, 1, 500);
            Regex regex;
            try
            {
                regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                return $"error: invalid regular expression: {ex.Message}";
            }

            Matcher? matcher = null;
            if (!string.IsNullOrWhiteSpace(glob))
            {
                matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
                matcher.AddInclude(glob.Trim().Replace('\\', '/'));
            }

            var sb = new StringBuilder();
            var matches = 0;
            var truncated = false;
            foreach (var rel in EnumerateFiles())
            {
                if (matcher is not null && !matcher.Match(rel).HasMatches)
                {
                    continue;
                }

                var full = Path.Combine(Paths.Root, rel);
                var info = new FileInfo(full);
                if (info.Length > MaxSearchFileBytes || IsBinary(full))
                {
                    continue;
                }

                var lineNo = 0;
                foreach (var line in File.ReadLines(full))
                {
                    lineNo++;
                    bool hit;
                    try
                    {
                        hit = regex.IsMatch(line);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return "error: the regular expression timed out; simplify it.";
                    }

                    if (!hit)
                    {
                        continue;
                    }

                    if (matches >= max)
                    {
                        truncated = true;
                        break;
                    }

                    matches++;
                    sb.Append(rel).Append(':').Append(lineNo).Append(": ").Append(TextUtil.TruncateEnd(line.Trim(), 200)).Append('\n');
                }

                if (truncated)
                {
                    break;
                }
            }

            if (matches == 0)
            {
                return "(no matches)";
            }

            if (truncated)
            {
                sb.Append($"…(stopped after {max} matches)\n");
            }

            return Bound(sb.ToString());
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    public string WriteFile(
        [Description("Path relative to the repository root.")] string path,
        [Description("Complete new content of the file.")] string content)
    {
        try
        {
            var full = Paths.ResolveForWrite(path);
            if (Directory.Exists(full))
            {
                return $"error: '{path}' is a directory.";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content ?? string.Empty, new UTF8Encoding(false));
            _actions?.Report(new CodingAction(CodingActionKind.Write, Paths.ToRelative(full)));
            return $"wrote {Paths.ToRelative(full)} ({(content ?? string.Empty).Length} characters)";
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    public string EditFile(
        [Description("Path relative to the repository root.")] string path,
        [Description("Exact text to replace; must occur exactly once.")] string oldText,
        [Description("Replacement text.")] string newText)
    {
        try
        {
            if (string.IsNullOrEmpty(oldText))
            {
                return "error: oldText is required; use write_file to create a file.";
            }

            var full = Paths.ResolveForWrite(path);
            if (!File.Exists(full))
            {
                return $"error: '{path}' does not exist.";
            }

            if (IsBinary(full))
            {
                return $"error: '{path}' is a binary file.";
            }

            var original = File.ReadAllText(full);
            var (text, find, replacement) = (original, oldText, newText ?? string.Empty);

            var count = CountOccurrences(text, find);
            if (count == 0)
            {
                // Tolerate line-ending differences between the model's text and the file.
                var usesCrLf = original.Contains("\r\n", StringComparison.Ordinal);
                text = original.Replace("\r\n", "\n", StringComparison.Ordinal);
                find = oldText.Replace("\r\n", "\n", StringComparison.Ordinal);
                replacement = (newText ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
                count = CountOccurrences(text, find);
                if (count == 0)
                {
                    return "error: oldText was not found in the file. Read the file again and copy the text exactly.";
                }

                if (usesCrLf)
                {
                    replacement = replacement.Replace("\n", "\r\n", StringComparison.Ordinal);
                    find = find.Replace("\n", "\r\n", StringComparison.Ordinal);
                    text = original;
                }
            }

            if (count > 1)
            {
                return $"error: oldText matches {count} places; include more surrounding context so it is unique.";
            }

            var idx = text.IndexOf(find, StringComparison.Ordinal);
            var updated = string.Concat(text.AsSpan(0, idx), replacement, text.AsSpan(idx + find.Length));
            File.WriteAllText(full, updated, new UTF8Encoding(false));
            _actions?.Report(new CodingAction(CodingActionKind.Edit, Paths.ToRelative(full)));
            return $"edited {Paths.ToRelative(full)}";
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    public async Task<string> RunAsync(
        [Description("Command line, e.g. 'dotnet test' or 'npm run lint'. Only allow-listed executables; no shell syntax.")] string command,
        CancellationToken cancellationToken = default)
    {
        if (_state.RunCount >= _options.Budget.MaxRuns)
        {
            return $"error: the run budget ({_options.Budget.MaxRuns} commands) is exhausted. Call done with a summary.";
        }

        _state.RunCount++;
        var (_, output) = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>Runs a command under policy; used by the <c>run</c> tool and by verification.</summary>
    public async Task<(bool Success, string Output)> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        ParsedCommand parsed;
        try
        {
            parsed = Commands.Parse(command);
        }
        catch (CodingPolicyException ex)
        {
            _state.CommandsRun.Add($"{command} (rejected)");
            _actions?.Report(new CodingAction(CodingActionKind.Run, $"{TextUtil.TruncateEnd(TextUtil.FirstLine(command), 500)} (rejected)"));
            return (false, "error: " + ex.Message);
        }

        _state.CommandsRun.Add(parsed.ToString());
        _logger.LogInformation("Task #{Task} runs: {Command}", _workspace.TaskId, parsed);

        using var activity = AgentTelemetry.Source.StartActivity("agent.sandbox.command", ActivityKind.Internal);
        activity
            .Tag("agent.task.id", _workspace.TaskId)
            .Tag("agent.sandbox.mode", Sandbox.Mode)
            .Tag("agent.command.executable", parsed.Executable);

        var startedAt = Stopwatch.GetTimestamp();
        ProcessResult result;
        try
        {
            result = await Sandbox.ExecuteAsync(
                parsed,
                TimeSpan.FromSeconds(Math.Max(1, _options.RunTimeoutSeconds)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RecordCommand("cancelled", startedAt, parsed);
            throw;
        }
        catch (Exception ex)
        {
            activity.Failed(ex);
            RecordCommand("error", startedAt, parsed);
            return (false, Error(ex));
        }

        RecordCommand(result.TimedOut ? "timeout" : result.Success ? "ok" : "failed", startedAt, parsed, result.ExitCode);
        activity.Tag("agent.command.exit_code", result.ExitCode);

        var sb = new StringBuilder();
        sb.Append("exit code: ").Append(result.ExitCode);
        if (result.TimedOut)
        {
            sb.Append(" (timed out)");
        }

        sb.Append('\n');
        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            sb.Append(result.StdOut.TrimEnd()).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(result.StdErr))
        {
            sb.Append("stderr:\n").Append(result.StdErr.TrimEnd()).Append('\n');
        }

        return (result.Success, Bound(sb.ToString()));
    }

    private void RecordCommand(string outcome, long from, ParsedCommand command, int? exitCode = null)
    {
        var tags = new TagList
        {
            { "mode", Sandbox.Mode },
            { "outcome", outcome },
            { "executable", command.Executable },
        };
        AgentTelemetry.SandboxCommands.Add(1, tags);
        AgentTelemetry.SandboxCommandDuration.Record(Stopwatch.GetElapsedTime(from).TotalSeconds, tags);

        var detail = exitCode is { } code && outcome != "ok"
            ? $"{command} ({outcome}, exit {code})"
            : $"{command} ({outcome})";
        _actions?.Report(new CodingAction(CodingActionKind.Run, TextUtil.TruncateEnd(detail, 500)));
    }

    public async Task<string> GitDiffAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var diff = await _git.DiffAsync(_workspace.RepoPath, stat: false, cancellationToken).ConfigureAwait(false);
            var status = await _git.StatusAsync(_workspace.RepoPath, cancellationToken).ConfigureAwait(false);
            var untracked = status.Split('\n').Where(l => l.StartsWith("??", StringComparison.Ordinal)).Select(l => l[3..].Trim()).ToList();

            var sb = new StringBuilder(diff);
            if (untracked.Count > 0)
            {
                sb.Append("\nUntracked files:\n");
                foreach (var f in untracked)
                {
                    sb.Append("  ").Append(f).Append('\n');
                }
            }

            return sb.Length == 0 ? "(no changes)" : Bound(sb.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    public async Task<string> GitStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _git.StatusAsync(_workspace.RepoPath, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(status) ? "(clean)" : Bound(status);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    public string Done([Description("What changed, what was verified, and anything left open.")] string summary)
    {
        _state.Completed = true;
        _state.Summary = string.IsNullOrWhiteSpace(summary) ? "(no summary given)" : summary.Trim();
        if (FunctionInvokingChatClient.CurrentContext is { } context)
        {
            context.Terminate = true;
        }

        return "ok";
    }

    /// <summary>Repository files as forward-slash relative paths, skipping VCS and build output directories.</summary>
    public IEnumerable<string> EnumerateFiles()
    {
        var pending = new Stack<string>();
        pending.Push(Paths.Root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var subDirs = new List<string>();
            foreach (var entry in entries.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Directory.Exists(entry))
                {
                    if (!SkippedDirectories.Contains(Path.GetFileName(entry)))
                    {
                        subDirs.Add(entry);
                    }
                }
                else
                {
                    yield return Paths.ToRelative(entry);
                }
            }

            for (var i = subDirs.Count - 1; i >= 0; i--)
            {
                pending.Push(subDirs[i]);
            }
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += value.Length;
        }

        return count;
    }

    private static bool IsBinary(string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        var buffer = new byte[BinaryProbeBytes];
        var read = stream.Read(buffer, 0, buffer.Length);
        return TextUtil.LooksBinary(buffer.AsSpan(0, read));
    }

    private string Bound(string text) => TextUtil.TruncateMiddle(text, _options.MaxToolOutputChars);

    private static string Error(Exception ex) => ex is CodingPolicyException ? "error: " + ex.Message : $"error: {ex.GetType().Name}: {ex.Message}";
}
