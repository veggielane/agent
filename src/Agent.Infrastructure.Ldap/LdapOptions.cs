using System.ComponentModel.DataAnnotations;

namespace Agent.Infrastructure.Ldap;

/// <summary>Direct Active Directory lookups; the fallback when Keycloak does not expose group membership.</summary>
public sealed class LdapOptions
{
    public const string SectionName = "Ldap";

    public bool Enabled { get; set; }

    /// <summary>Domain controller host name, e.g. <c>dc01.corp.local</c>.</summary>
    public string Server { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 636;

    public bool UseSsl { get; set; } = true;

    /// <summary>Search base, e.g. <c>DC=corp,DC=local</c>.</summary>
    public string BaseDn { get; set; } = string.Empty;

    /// <summary>Service account DN or UPN used to bind; anonymous when empty.</summary>
    public string? BindDn { get; set; }

    public string? BindPassword { get; set; }

    /// <summary>Filter locating a user; <c>{0}</c> is replaced with the escaped candidate (username, UPN, e-mail, channel id).</summary>
    public string UserSearchFilter { get; set; } = "(|(sAMAccountName={0})(userPrincipalName={0})(mail={0}))";

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 15;
}
