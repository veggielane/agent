namespace Agent.Coding.Sandbox;

/// <summary>The v1 behaviour: commands run on the worker host as child processes with a scrubbed environment.</summary>
public sealed class ProcessSandbox : ISandbox
{
    private readonly IProcessRunner _processes;

    public ProcessSandbox(IProcessRunner processes) => _processes = processes;

    public string Name => "process";

    public Task<ISandboxSession> StartAsync(Workspace workspace, CancellationToken cancellationToken)
        => Task.FromResult<ISandboxSession>(new ProcessSandboxSession(_processes, workspace));
}

public sealed class ProcessSandboxSession : ISandboxSession
{
    private readonly IProcessRunner _processes;
    private readonly Workspace _workspace;

    public ProcessSandboxSession(IProcessRunner processes, Workspace workspace)
    {
        _processes = processes;
        _workspace = workspace;
    }

    public string Description => "a host process in the repository directory (scrubbed environment, no container)";

    public Task<ProcessResult> ExecuteAsync(ParsedCommand command, TimeSpan timeout, CancellationToken cancellationToken)
        => _processes.RunAsync(command.Executable, command.Arguments, _workspace.RepoPath, null, timeout, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
