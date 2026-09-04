using System.Diagnostics;
using Agent.Coding;
using Microsoft.Extensions.Options;

namespace Agent.Coding.Tests;

/// <summary>A unique temp directory removed on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agent-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relative, string content)
    {
        var full = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            WorkspaceManager.DeleteDirectory(Path);
        }
        catch (Exception)
        {
            // best effort
        }
    }
}

public sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T value)
    {
        CurrentValue = value;
    }

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public static class TestOptions
{
    public static CodingOptions Coding(string workspaceRoot, Action<CodingOptions>? configure = null)
    {
        var options = new CodingOptions { WorkspaceRoot = workspaceRoot, UseBlobFilter = false, RunTimeoutSeconds = 120 };
        configure?.Invoke(options);
        return options;
    }

    public static TestOptionsMonitor<CodingOptions> Monitor(CodingOptions options) => new(options);
}

/// <summary>Runs the real git binary for test setup and assertions; tests skip when git is not installed.</summary>
public static class GitTestHelper
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            var (code, _) = Run(Path.GetTempPath(), "--version");
            return code == 0;
        }
        catch (Exception)
        {
            return false;
        }
    });

    public static bool IsAvailable => Available.Value;

    public static void SkipIfMissing()
    {
        if (!IsAvailable)
        {
            Assert.Skip("git is not installed on this machine.");
        }
    }

    /// <summary>Creates a bare repository with one commit on <c>main</c> and returns its file:// URL.</summary>
    public static string CreateBareRepoWithCommit(string directory, string? extraFile = null, string? extraContent = null)
    {
        var bare = Path.Combine(directory, "remote.git");
        var seed = Path.Combine(directory, "seed");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(seed);

        Must(Run(bare, "init", "--bare", "-b", "main", "."));
        Must(Run(seed, "init", "-b", "main", "."));
        File.WriteAllText(Path.Combine(seed, "README.md"), "# Seed\n");
        if (extraFile is not null)
        {
            var full = Path.Combine(seed, extraFile.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, extraContent ?? string.Empty);
        }

        Must(Run(seed, "add", "-A"));
        Must(Run(seed, "commit", "-q", "-m", "initial"));
        Must(Run(seed, "remote", "add", "origin", bare));
        Must(Run(seed, "push", "-q", "origin", "main"));
        return ToFileUrl(bare);
    }

    public static string ToFileUrl(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public static string BarePath(string directory) => Path.Combine(directory, "remote.git");

    public static (int ExitCode, string Output) Run(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var a in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false" })
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    private static void Must((int ExitCode, string Output) result)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("git setup failed: " + result.Output);
        }
    }
}
