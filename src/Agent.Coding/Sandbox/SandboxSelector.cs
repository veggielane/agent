using Microsoft.Extensions.Options;

namespace Agent.Coding.Sandbox;

/// <summary>Picks the sandbox for each run from the current <see cref="SandboxOptions.Mode"/>, so the setting hot-reloads.</summary>
public sealed class SandboxSelector : ISandbox
{
    private readonly IOptionsMonitor<CodingOptions> _options;
    private readonly ProcessSandbox _process;
    private readonly DockerSandbox _docker;

    public SandboxSelector(IOptionsMonitor<CodingOptions> options, ProcessSandbox process, DockerSandbox docker)
    {
        _options = options;
        _process = process;
        _docker = docker;
    }

    public string Name => Current.Name;

    private ISandbox Current => _options.CurrentValue.Sandbox.Mode == SandboxMode.Docker ? _docker : _process;

    public Task<ISandboxSession> StartAsync(Workspace workspace, CancellationToken cancellationToken)
        => Current.StartAsync(workspace, cancellationToken);
}
