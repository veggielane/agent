using System.ComponentModel.DataAnnotations;

namespace Agent.Infrastructure.Keycloak;

public sealed class KeycloakAdminOptions
{
    /// <summary>Confidential service client with the <c>view-users</c> role.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Admin REST base, e.g. <c>https://sso.corp.local/admin/realms/corp</c>. Derived from the authority when null.</summary>
    public string? BaseUrl { get; set; }
}

public sealed class KeycloakOptions
{
    public const string SectionName = "Keycloak";

    private const string RealmsSegment = "/realms/";

    /// <summary>OIDC issuer of the realm, e.g. <c>https://sso.corp.local/realms/corp</c>.</summary>
    [Required]
    public string Authority { get; set; } = string.Empty;

    /// <summary>Audience the host API expects in bearer tokens.</summary>
    public string ApiAudience { get; set; } = "agent-api";

    /// <summary>Public client used by the CLI's device flow.</summary>
    public string CliClientId { get; set; } = "agent-cli";

    public KeycloakAdminOptions Admin { get; set; } = new();

    /// <summary>Token claim carrying group paths (Keycloak "Group Membership" mapper).</summary>
    public string GroupClaim { get; set; } = "groups";

    /// <summary>Dot-separated path to the realm roles array inside the token.</summary>
    public string RolesClaimPath { get; set; } = "realm_access.roles";

    /// <summary>Also report realm roles (as <c>role:{name}</c>) so <c>Authorization.Roles</c> can map them.</summary>
    public bool IncludeRealmRoles { get; set; } = true;

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Realm name parsed from <see cref="Authority"/>.</summary>
    public string Realm
    {
        get
        {
            var (_, realm) = SplitAuthority();
            return realm;
        }
    }

    /// <summary>Authority without a trailing slash.</summary>
    public string AuthorityBase => Authority.TrimEnd('/');

    /// <summary>Admin REST base for this realm, with a trailing slash.</summary>
    public Uri AdminBaseUri
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Admin.BaseUrl))
            {
                return new Uri(Admin.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            }

            var (prefix, realm) = SplitAuthority();
            return new Uri($"{prefix}/admin/realms/{realm}/", UriKind.Absolute);
        }
    }

    public string TokenEndpoint => $"{AuthorityBase}/protocol/openid-connect/token";

    public string DiscoveryEndpoint => $"{AuthorityBase}/.well-known/openid-configuration";

    /// <summary>Splits <c>https://host[/prefix]/realms/{realm}</c> into the part before <c>/realms/</c> and the realm.</summary>
    private (string Prefix, string Realm) SplitAuthority()
    {
        var authority = AuthorityBase;
        var index = authority.LastIndexOf(RealmsSegment, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(Authority)} must look like https://host/realms/{{realm}} (got '{Authority}').");
        }

        var prefix = authority[..index];
        var realm = authority[(index + RealmsSegment.Length)..].Trim('/');
        if (realm.Length == 0 || realm.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(Authority)} must end with /realms/{{realm}} (got '{Authority}').");
        }

        return (prefix, realm);
    }
}
