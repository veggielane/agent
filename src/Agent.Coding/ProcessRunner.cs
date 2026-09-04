using System.Collections;
using System.Text;
using CliWrap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Coding;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;

    /// <summary>Stdout and stderr joined, for feeding back to the model or into an error.</summary>
    public string CombinedOutput
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StdErr))
            {
                return StdOut;
            }

            return string.IsNullOrWhiteSpace(StdOut) ? StdErr : StdOut + "\n" + StdErr;
        }
    }
}

/// <summary>Starts a process directly (no shell) with a scrubbed environment, a timeout, and bounded output.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput = null);
}

public sealed class CliWrapProcessRunner : IProcessRunner
{
    /// <summary>Variables copied from the host process; everything else (tokens, connection strings) is dropped.</summary>
    public static readonly string[] InheritedVariables =
    [
        "PATH", "PATHEXT", "SystemRoot", "windir", "ComSpec", "SystemDrive", "ProgramData", "TEMP", "TMP", "TMPDIR", "HOME", "HOMEDRIVE", "HOMEPATH",
        "USERPROFILE", "USERNAME", "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "DOTNET_ROOT",
        "NUGET_PACKAGES", "LANG", "LC_ALL", "TERM",
    ];

    public static readonly IReadOnlyDictionary<string, string> ForcedVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["CI"] = "1",
    };

    private readonly IOptionsMonitor<CodingOptions> _options;
    private readonly ILogger<CliWrapProcessRunner> _logger;

    public CliWrapProcessRunner(IOptionsMonitor<CodingOptions> options, ILogger<CliWrapProcessRunner> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Builds the environment for a child process: inherited allow-list + forced values + caller extras.
    /// Variables not in the result are mapped to <see langword="null"/>, which CliWrap turns into removal.
    /// </summary>
    public static Dictionary<string, string?> BuildEnvironment(IReadOnlyDictionary<string, string>? extra)
    {
        var keep = new HashSet<string>(InheritedVariables, StringComparer.OrdinalIgnoreCase);
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            env[key] = keep.Contains(key) ? entry.Value?.ToString() : null;
        }

        foreach (var (k, v) in ForcedVariables)
        {
            env[k] = v;
        }

        if (extra is not null)
        {
            foreach (var (k, v) in extra)
            {
                env[k] = v;
            }
        }

        return env;
    }

    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        var maxChars = Math.Max(500, _options.CurrentValue.MaxToolOutputChars);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var command = Cli.Wrap(executable)
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithEnvironmentVariables(BuildEnvironment(environment))
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr));

        if (standardInput is not null)
        {
            command = command.WithStandardInputPipe(PipeSource.FromString(standardInput, new UTF8Encoding(false)));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(timeout);
        }

        _logger.LogDebug("Running {Executable} {Arguments} in {Dir}", executable, string.Join(' ', arguments), workingDirectory);

        try
        {
            var result = await command.ExecuteAsync(timeoutCts.Token).ConfigureAwait(false);
            return new ProcessResult(result.ExitCode, Bound(stdout, maxChars), Bound(stderr, maxChars), false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("{Executable} timed out after {Timeout}", executable, timeout);
            return new ProcessResult(-1, Bound(stdout, maxChars), Bound(stderr, maxChars) + $"\n[process timed out after {timeout.TotalSeconds:0}s and was killed]", true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            return new ProcessResult(-1, string.Empty, $"could not start '{executable}': {ex.Message}", false);
        }
    }

    private static string Bound(StringBuilder sb, int maxChars) => TextUtil.TruncateMiddle(sb.ToString(), maxChars);
}
