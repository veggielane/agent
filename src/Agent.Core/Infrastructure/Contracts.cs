namespace Agent.Core.Infrastructure;

/// <summary>Something <c>!reload</c> can refresh: the command registry, MCP servers, prompt cache.</summary>
public interface IReloadable
{
    string Name { get; }

    Task ReloadAsync(CancellationToken cancellationToken);
}

/// <summary>Adds lines to <c>!status</c>.</summary>
public interface IStatusContributor
{
    string Name { get; }

    Task<string> GetStatusAsync(CancellationToken cancellationToken);
}
