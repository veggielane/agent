using System.Globalization;
using System.Security.Cryptography;
using Agent.Core.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Coding.Sandbox;

/// <summary>
/// One Linux container per task. The repository directory is bind-mounted at <see cref="SandboxOptions.WorkDir"/>;
/// the container idles on <c>sleep infinity</c> and every command is a <c>docker exec</c>. The docker CLI is
/// driven through <see cref="IProcessRunner"/>, so the exact invocations are testable without a daemon.
/// </summary>
public sealed class DockerSandbox : ISandbox, IStatusContributor
{
    /// <summary>Exit code coreutils <c>timeout -s KILL</c> reports when it killed the command.</summary>
    public const int KilledByTimeoutExitCode = 137;

    private static readonly IReadOnlyDictionary<string, string> ContainerEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["CI"] = "1",
        // The mounted repository is owned by the host user; without this git refuses to read it.
        ["GIT_CONFIG_COUNT"] = "1",
        ["GIT_CONFIG_KEY_0"] = "safe.directory",
        ["GIT_CONFIG_VALUE_0"] = "*",
    };

    private readonly IProcessRunner _processes;
    private readonly IOptionsMonitor<CodingOptions> _options;
    private readonly ILogger<DockerSandbox> _logger;

    public DockerSandbox(IProcessRunner processes, IOptionsMonitor<CodingOptions> options, ILogger<DockerSandbox> logger)
    {
        _processes = processes;
        _options = options;
        _logger = logger;
    }

    public string Name => "docker";

    string IStatusContributor.Name => "Sandbox";

    public async Task<ISandboxSession> StartAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.Sandbox;

        // Throws when the repository asked for something the host does not grant, so the task fails with
        // that message rather than quietly running somewhere else.
        var resolved = SandboxPolicy.Resolve(options, workspace.Profile);
        var name = ContainerName(workspace.TaskId);
        var arguments = BuildRunArguments(options, workspace, resolved, name);

        _logger.LogInformation(
            "Starting sandbox container {Container} from {Image} ({Source}) for task #{Task}",
            name,
            resolved.Image,
            resolved.Source,
            workspace.TaskId);

        ProcessResult result;
        try
        {
            result = await _processes.RunAsync(options.DockerPath, arguments, workspace.RepoPath, null, TimeSpan.FromSeconds(Math.Max(10, options.StartTimeoutSeconds)), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SandboxException($"Could not start docker: {ex.Message}", ex);
        }

        if (!result.Success)
        {
            // A workspace root the daemon may not bind-mount makes `docker run` hang rather than fail.
            var reason = result.TimedOut
                ? $"timed out after {options.StartTimeoutSeconds}s — check the image pull and that the daemon may bind-mount '{Path.GetFullPath(workspace.RepoPath)}'"
                : $"exit code {result.ExitCode}";
            throw new SandboxException($"docker run failed ({reason}): {result.CombinedOutput.Trim()}");
        }

        return new DockerSandboxSession(_processes, options, workspace, name, resolved, _logger);
    }

    public async Task<string> GetStatusAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.Sandbox;
        if (options.Mode != SandboxMode.Docker)
        {
            return $"{options.Mode} (docker not in use)";
        }

        try
        {
            var result = await _processes.RunAsync(options.DockerPath, ["version", "--format", "{{.Server.Version}}"], Path.GetTempPath(), null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return result.Success
                ? $"docker {result.StdOut.Trim()}, default image {options.DefaultImage}, network {options.Network}"
                : $"docker unavailable: {TextUtil.FirstLine(result.CombinedOutput)}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"docker unavailable: {ex.Message}";
        }
    }

    public static string ContainerName(int taskId)
        => $"agent-task-{taskId}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";

    /// <summary>Picks the image for the repository's toolchain, falling back to <see cref="SandboxOptions.DefaultImage"/>.</summary>
    public static string ResolveImage(SandboxOptions options, RepoProfile profile)
    {
        var toolchain = ToolchainOf(profile);
        if (toolchain is not null && options.Images.TryGetValue(toolchain, out var image) && !string.IsNullOrWhiteSpace(image))
        {
            return image;
        }

        return options.DefaultImage;
    }

    /// <summary>"dotnet", "node", "python", "go", "rust", or null, from the first executable of the build/test/lint commands.</summary>
    public static string? ToolchainOf(RepoProfile profile)
    {
        foreach (var command in new[] { profile.BuildCommand, profile.TestCommand, profile.LintCommand })
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            var first = command.Trim().Split(' ', 2)[0].ToLowerInvariant();
            if (first.EndsWith(".exe", StringComparison.Ordinal))
            {
                first = first[..^4];
            }

            var toolchain = first switch
            {
                "dotnet" => "dotnet",
                "npm" or "npx" or "node" or "yarn" or "pnpm" => "node",
                "python" or "python3" or "pytest" or "pip" or "uv" or "poetry" => "python",
                "go" => "go",
                "cargo" or "rustc" => "rust",
                _ => null,
            };

            if (toolchain is not null)
            {
                return toolchain;
            }
        }

        return null;
    }

    public static IReadOnlyList<string> BuildRunArguments(SandboxOptions options, Workspace workspace, ResolvedSandbox resolved, string containerName)
    {
        var args = new List<string>
        {
            "run",
            "--detach",
            "--name", containerName,
            "--label", $"agent.task={workspace.TaskId}",
            "--network", resolved.Network,
            "--memory", resolved.Memory,
            "--cpus", resolved.Cpus.ToString(CultureInfo.InvariantCulture),
            "--pids-limit", options.PidsLimit.ToString(CultureInfo.InvariantCulture),
            "--security-opt", "no-new-privileges",
            "--cap-drop", "ALL",
        };

        if (options.Init)
        {
            args.Add("--init");
        }

        if (options.ReadOnlyRootFilesystem)
        {
            args.Add("--read-only");
        }

        if (!string.IsNullOrWhiteSpace(options.TmpfsSize))
        {
            args.Add("--tmpfs");
            args.Add($"/tmp:rw,exec,size={options.TmpfsSize}");
        }

        if (!string.IsNullOrWhiteSpace(options.User))
        {
            args.Add("--user");
            args.Add(options.User);
        }

        args.Add("--volume");
        args.Add($"{Path.GetFullPath(workspace.RepoPath)}:{options.WorkDir}");
        args.Add("--workdir");
        args.Add(options.WorkDir);

        foreach (var volume in resolved.Volumes.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            args.Add("--volume");
            args.Add(volume.Trim());
        }

        foreach (var (key, value) in BuildEnvironment(resolved.Env))
        {
            args.Add("--env");
            args.Add($"{key}={value}");
        }

        args.AddRange(options.ExtraArgs.Where(a => !string.IsNullOrWhiteSpace(a)));
        args.Add(resolved.Image);
        args.Add("sleep");
        args.Add("infinity");
        return args;
    }

    /// <summary>The forced safety baseline, with the resolved (profile + repository + host) values layered on top.</summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironment(IReadOnlyDictionary<string, string>? extra)
    {
        var env = new Dictionary<string, string>(ContainerEnvironment, StringComparer.Ordinal);
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    env[key.Trim()] = value ?? string.Empty;
                }
            }
        }

        return env;
    }

    public static IReadOnlyList<string> BuildExecArguments(SandboxOptions options, string containerName, ParsedCommand command, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null)
    {
        var args = new List<string> { "exec", "--workdir", options.WorkDir };
        if (environment is not null)
        {
            foreach (var (key, value) in environment.Where(e => !string.IsNullOrWhiteSpace(e.Key)))
            {
                args.Add("--env");
                args.Add($"{key.Trim()}={value}");
            }
        }

        args.Add(containerName);
        if (options.UseTimeoutWrapper && timeout > TimeSpan.Zero)
        {
            args.Add("timeout");
            args.Add("-s");
            args.Add("KILL");
            args.Add(((int)Math.Ceiling(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture));
        }

        args.Add(command.Executable);
        args.AddRange(command.Arguments);
        return args;
    }
}

public sealed class DockerSandboxSession : ISandboxSession
{
    private readonly IProcessRunner _processes;
    private readonly SandboxOptions _options;
    private readonly Workspace _workspace;
    private readonly ILogger _logger;
    private bool _disposed;

    public DockerSandboxSession(IProcessRunner processes, SandboxOptions options, Workspace workspace, string containerName, ResolvedSandbox resolved, ILogger logger)
    {
        _processes = processes;
        _options = options;
        _workspace = workspace;
        ContainerName = containerName;
        Resolved = resolved;
        _logger = logger;
    }

    public string ContainerName { get; }

    public ResolvedSandbox Resolved { get; }

    public string Image => Resolved.Image;

    public string Description
        => $"an isolated container ({Image}, from {Resolved.Source}{(Resolved.Network == "none" ? ", no network access" : string.Empty)}); the repository is mounted at {_options.WorkDir}, which is the working directory";

    public string Mode => "docker";

    public string PathInSandbox(string relativePath)
        => _options.WorkDir.TrimEnd('/') + "/" + relativePath.Replace('\\', '/').TrimStart('/');

    public async Task<ProcessResult> ExecuteAsync(ParsedCommand command, TimeSpan timeout, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var args = DockerSandbox.BuildExecArguments(_options, ContainerName, command, timeout, environment);
        var hostTimeout = timeout > TimeSpan.Zero ? timeout + TimeSpan.FromSeconds(15) : timeout;
        var result = await _processes.RunAsync(_options.DockerPath, args, _workspace.RepoPath, null, hostTimeout, cancellationToken).ConfigureAwait(false);

        if (!result.TimedOut && _options.UseTimeoutWrapper && result.ExitCode == DockerSandbox.KilledByTimeoutExitCode)
        {
            return result with { TimedOut = true, StdErr = result.StdErr + $"\n[command killed after {timeout.TotalSeconds:0}s inside the container]" };
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            var result = await _processes.RunAsync(_options.DockerPath, ["rm", "--force", ContainerName], _workspace.RepoPath, null, TimeSpan.FromSeconds(60), CancellationToken.None).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogWarning("Removing sandbox container {Container} failed: {Output}", ContainerName, TextUtil.FirstLine(result.CombinedOutput));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Removing sandbox container {Container} failed", ContainerName);
        }
    }
}
