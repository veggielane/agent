using Agent.Infrastructure.Keycloak.DeviceFlow;

namespace Agent.Infrastructure.Keycloak.TokenCache;

/// <summary>Per-user storage for the CLI's tokens.</summary>
public interface ITokenCache
{
    /// <summary>The cached tokens, or null when none are stored or the file cannot be read.</summary>
    Task<TokenSet?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(TokenSet tokens, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
