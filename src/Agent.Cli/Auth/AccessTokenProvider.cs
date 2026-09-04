using Agent.Infrastructure.Keycloak.DeviceFlow;
using Agent.Infrastructure.Keycloak.TokenCache;

namespace Agent.Cli.Auth;

public interface IAccessTokenProvider
{
    /// <summary>Returns a valid access token, refreshing silently when needed. Throws when the user must log in.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

public sealed class KeycloakAccessTokenProvider : IAccessTokenProvider
{
    private readonly ITokenCache _cache;
    private readonly IKeycloakDeviceFlowClient _keycloak;

    public KeycloakAccessTokenProvider(ITokenCache cache, IKeycloakDeviceFlowClient keycloak)
    {
        _cache = cache;
        _keycloak = keycloak;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var tokens = await _cache.LoadAsync(cancellationToken)
            ?? throw new Backends.CliException("Not logged in. Run `agent login` first.");

        if (tokens.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return tokens.AccessToken;
        }

        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            throw new Backends.CliException("Session expired. Run `agent login` again.");
        }

        try
        {
            var refreshed = await _keycloak.RefreshAsync(tokens.RefreshToken, cancellationToken);
            await _cache.SaveAsync(refreshed, cancellationToken);
            return refreshed.AccessToken;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            throw new Backends.CliException($"Could not refresh the session ({ex.Message}). Run `agent login` again.");
        }
    }
}
