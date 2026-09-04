namespace Agent.Infrastructure.Keycloak.DeviceFlow;

/// <summary>The subset of the OpenID Connect discovery document the CLI needs.</summary>
public sealed record OidcDiscovery(
    string Issuer,
    string TokenEndpoint,
    string DeviceAuthorizationEndpoint,
    string? AuthorizationEndpoint = null,
    string? EndSessionEndpoint = null);

/// <summary>Result of the device authorization request (RFC 8628 §3.2).</summary>
public sealed record DeviceAuthorization(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string? VerificationUriComplete,
    DateTimeOffset ExpiresAt,
    TimeSpan Interval);

public sealed record TokenSet(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    string? IdToken = null)
{
    /// <summary>True when the access token expires within <paramref name="skew"/> (default 30 s).</summary>
    public bool IsExpired(TimeProvider? timeProvider = null, TimeSpan? skew = null)
        => (timeProvider ?? TimeProvider.System).GetUtcNow() >= ExpiresAt - (skew ?? TimeSpan.FromSeconds(30));
}

/// <summary>A terminal OAuth error from the device or token endpoint (<c>expired_token</c>, <c>access_denied</c>, ...).</summary>
public sealed class DeviceFlowException : Exception
{
    public DeviceFlowException(string error, string? description)
        : base(description is null ? error : $"{error}: {description}")
    {
        Error = error;
        Description = description;
    }

    public string Error { get; }

    public string? Description { get; }
}
