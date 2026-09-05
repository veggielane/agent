namespace Agent.Coding.Sandbox;

/// <summary>Creates one execution environment per coding run.</summary>
public interface ISandbox
{
    string Name { get; }

    Task<ISandboxSession> StartAsync(Workspace workspace, CancellationToken cancellationToken);
}

/// <summary>Where the model's commands execute for one task. Disposing tears the environment down.</summary>
public interface ISandboxSession : IAsyncDisposable
{
    /// <summary>Human-readable description used in the system prompt and task events.</summary>
    string Description { get; }

    /// <summary>Short identifier for logs and metrics: <c>process</c> or <c>docker</c>.</summary>
    string Mode { get; }

    /// <summary>Runs an already policy-checked command in the repository directory.</summary>
    /// <param name="environment">
    /// Extra environment for this command only. Never put credentials here: on the container path these
    /// values appear on the docker command line.
    /// </param>
    Task<ProcessResult> ExecuteAsync(ParsedCommand command, TimeSpan timeout, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null);

    /// <summary>
    /// Translates a repository-relative path into the absolute path a command sees inside the sandbox:
    /// the host path for a process, the mount point for a container.
    /// </summary>
    string PathInSandbox(string relativePath);
}

public sealed class SandboxException : Exception
{
    public SandboxException(string message)
        : base(message)
    {
    }

    public SandboxException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
