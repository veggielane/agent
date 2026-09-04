namespace Agent.Infrastructure.Keycloak.DeviceFlow;

/// <summary>OAuth 2.0 device authorization flow (RFC 8628) against the configured Keycloak realm, for the CLI's <c>agent login</c>.</summary>
public interface IKeycloakDeviceFlowClient
{
    Task<OidcDiscovery> DiscoverAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests a device + user code. The caller shows <see cref="DeviceAuthorization.VerificationUri"/> and <see cref="DeviceAuthorization.UserCode"/>.</summary>
    Task<DeviceAuthorization> StartAsync(string scope = "openid profile email", CancellationToken cancellationToken = default);

    /// <summary>Polls the token endpoint until the user approves. Throws <see cref="DeviceFlowException"/> on denial or expiry.</summary>
    Task<TokenSet> PollAsync(DeviceAuthorization authorization, CancellationToken cancellationToken = default);

    Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
}
