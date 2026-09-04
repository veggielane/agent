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

    /// <summary>Runs an already policy-checked command in the repository directory.</summary>
    Task<ProcessResult> ExecuteAsync(ParsedCommand command, TimeSpan timeout, CancellationToken cancellationToken);
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
