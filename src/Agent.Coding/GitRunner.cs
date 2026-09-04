using System.Text;
using Microsoft.Extensions.Logging;

namespace Agent.Coding;

/// <summary>
/// Git operations the orchestrator performs. Credentials travel through environment variables of the
/// individual clone/fetch/push process only; they never appear on a command line or in the repo config.
/// </summary>
public interface IGitRunner
{
    Task CloneAsync(string url, string directory, (string Username, string Token)? credentials, int depth, bool blobFilter, CancellationToken cancellationToken);

    /// <summary><c>git checkout -b name [from]</c>.</summary>
    Task CreateBranchAsync(string directory, string name, string? from, CancellationToken cancellationToken);

    Task CheckoutAsync(string directory, string branch, CancellationToken cancellationToken);

    /// <summary><c>git fetch origin [refspec]</c>; <paramref name="depth"/> &gt; 0 adds <c>--depth</c>.</summary>
    Task FetchAsync(string directory, string? refspec, (string Username, string Token)? credentials, int depth, CancellationToken cancellationToken);

    /// <summary><c>git status --porcelain</c> output.</summary>
    Task<string> StatusAsync(string directory, CancellationToken cancellationToken);

    /// <summary><c>git diff HEAD</c>, optionally <c>--stat</c>.</summary>
    Task<string> DiffAsync(string directory, bool stat, CancellationToken cancellationToken);

    Task<bool> HasChangesAsync(string directory, CancellationToken cancellationToken);

    Task AddAllAsync(string directory, CancellationToken cancellationToken);

    Task CommitAsync(string directory, string message, string authorName, string authorEmail, CancellationToken cancellationToken);

    Task PushAsync(string directory, string branch, (string Username, string Token)? credentials, bool setUpstream, CancellationToken cancellationToken);

    Task<string> CurrentBranchAsync(string directory, CancellationToken cancellationToken);

    Task<bool> BranchExistsOnRemoteAsync(string directory, string branch, (string Username, string Token)? credentials, CancellationToken cancellationToken);
}

public sealed class GitRunner : IGitRunner
{
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromMinutes(15);

    private readonly IProcessRunner _processes;
    private readonly ILogger<GitRunner> _logger;

    public GitRunner(IProcessRunner processes, ILogger<GitRunner> logger)
    {
        _processes = processes;
        _logger = logger;
    }

    /// <summary>Environment that makes git send a basic-auth header for HTTP(S) remotes without touching any config file.</summary>
    public static IReadOnlyDictionary<string, string>? CredentialEnvironment((string Username, string Token)? credentials)
    {
        if (credentials is null)
        {
            return null;
        }

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Value.Username}:{credentials.Value.Token}"));
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "http.extraheader",
            ["GIT_CONFIG_VALUE_0"] = "Authorization: Basic " + basic,
        };
    }

    /// <summary>Paths from <c>git status --porcelain</c> output (renames yield the new name).</summary>
    public static IReadOnlyList<string> ParseStatus(string porcelain)
    {
        var files = new List<string>();
        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 4 || line[2] != ' ')
            {
                continue;
            }

            var path = line[3..];
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                path = path[(arrow + 4)..];
            }

            path = path.Trim();
            if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            {
                path = path[1..^1];
            }

            if (path.Length > 0)
            {
                files.Add(path);
            }
        }

        return files;
    }

    public async Task CloneAsync(string url, string directory, (string Username, string Token)? credentials, int depth, bool blobFilter, CancellationToken cancellationToken)
    {
        var args = new List<string> { "clone", "--no-tags" };
        if (depth > 0)
        {
            // Keep the remote refspec wide so other branches (base branch, existing work branch) can still be fetched and tracked.
            args.Add("--depth");
            args.Add(depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Add("--no-single-branch");
        }

        if (blobFilter)
        {
            args.Add("--filter=blob:none");
        }

        args.Add("--");
        args.Add(url);
        args.Add(directory);

        var parent = Path.GetDirectoryName(Path.GetFullPath(directory)) ?? Path.GetTempPath();
        Directory.CreateDirectory(parent);
        await RunAsync(parent, args, CredentialEnvironment(credentials), RemoteTimeout, cancellationToken).ConfigureAwait(false);
    }

    public Task CreateBranchAsync(string directory, string name, string? from, CancellationToken cancellationToken)
    {
        var args = new List<string> { "checkout", "-b", name };
        if (!string.IsNullOrWhiteSpace(from))
        {
            args.Add(from);
        }

        return RunAsync(directory, args, null, LocalTimeout, cancellationToken);
    }

    public async Task CheckoutAsync(string directory, string branch, CancellationToken cancellationToken)
    {
        // A shallow/single-branch clone cannot guess the remote branch, so be explicit when no local branch exists.
        var local = await _processes.RunAsync("git", ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], directory, null, LocalTimeout, cancellationToken).ConfigureAwait(false);
        if (local.Success)
        {
            await RunAsync(directory, ["checkout", branch], null, LocalTimeout, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RunAsync(directory, ["checkout", "-b", branch, $"origin/{branch}"], null, LocalTimeout, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task FetchAsync(string directory, string? refspec, (string Username, string Token)? credentials, int depth, CancellationToken cancellationToken)
    {
        var args = new List<string> { "fetch", "--no-tags" };
        if (depth > 0)
        {
            args.Add("--depth");
            args.Add(depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        args.Add("origin");
        if (!string.IsNullOrWhiteSpace(refspec))
        {
            args.Add(refspec);
        }

        return RunAsync(directory, args, CredentialEnvironment(credentials), RemoteTimeout, cancellationToken);
    }

    public async Task<string> StatusAsync(string directory, CancellationToken cancellationToken)
        => (await RunAsync(directory, ["status", "--porcelain", "--untracked-files=all"], null, LocalTimeout, cancellationToken).ConfigureAwait(false)).StdOut;

    public async Task<string> DiffAsync(string directory, bool stat, CancellationToken cancellationToken)
    {
        var args = new List<string> { "--no-pager", "diff", "HEAD" };
        if (stat)
        {
            args.Add("--stat");
        }

        return (await RunAsync(directory, args, null, LocalTimeout, cancellationToken).ConfigureAwait(false)).StdOut;
    }

    public async Task<bool> HasChangesAsync(string directory, CancellationToken cancellationToken)
        => !string.IsNullOrWhiteSpace(await StatusAsync(directory, cancellationToken).ConfigureAwait(false));

    public Task AddAllAsync(string directory, CancellationToken cancellationToken)
        => RunAsync(directory, ["add", "-A", "--", "."], null, LocalTimeout, cancellationToken);

    public Task CommitAsync(string directory, string message, string authorName, string authorEmail, CancellationToken cancellationToken)
        => RunAsync(
            directory,
            ["-c", $"user.name={authorName}", "-c", $"user.email={authorEmail}", "-c", "commit.gpgsign=false", "commit", "--no-verify", "-F", "-"],
            null,
            LocalTimeout,
            cancellationToken,
            standardInput: message.EndsWith('\n') ? message : message + "\n");

    public Task PushAsync(string directory, string branch, (string Username, string Token)? credentials, bool setUpstream, CancellationToken cancellationToken)
    {
        var args = new List<string> { "push" };
        if (setUpstream)
        {
            args.Add("-u");
        }

        args.Add("origin");
        args.Add($"{branch}:{branch}");
        return RunAsync(directory, args, CredentialEnvironment(credentials), RemoteTimeout, cancellationToken);
    }

    public async Task<string> CurrentBranchAsync(string directory, CancellationToken cancellationToken)
        => (await RunAsync(directory, ["rev-parse", "--abbrev-ref", "HEAD"], null, LocalTimeout, cancellationToken).ConfigureAwait(false)).StdOut.Trim();

    public async Task<bool> BranchExistsOnRemoteAsync(string directory, string branch, (string Username, string Token)? credentials, CancellationToken cancellationToken)
    {
        var result = await RunAsync(directory, ["ls-remote", "--heads", "origin", branch], CredentialEnvironment(credentials), RemoteTimeout, cancellationToken).ConfigureAwait(false);
        return result.StdOut.Split('\n').Any(l => l.TrimEnd().EndsWith("refs/heads/" + branch, StringComparison.Ordinal));
    }

    private async Task<ProcessResult> RunAsync(string directory, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, TimeSpan timeout, CancellationToken cancellationToken, string? standardInput = null)
    {
        var result = await _processes.RunAsync("git", args, directory, env, timeout, cancellationToken, standardInput).ConfigureAwait(false);
        if (!result.Success)
        {
            var summary = string.Join(' ', args.Take(2));
            _logger.LogWarning("git {Command} failed ({ExitCode}): {Output}", summary, result.ExitCode, TextUtil.TruncateEnd(result.CombinedOutput, 500));
            throw new GitException(summary, result.ExitCode, result.CombinedOutput);
        }

        return result;
    }
}
