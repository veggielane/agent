using System.ComponentModel.DataAnnotations;

namespace Agent.Core.Authorization;

public enum DenyBehaviour
{
    /// <summary>Ignore the request. Private conversations (DMs, CLI) still get a one-line refusal.</summary>
    Silent,

    /// <summary>Always reply with a short refusal.</summary>
    Reply,
}

public sealed class AuthorizationOptions
{
    public const string SectionName = "Authorization";

    /// <summary>Name of the <see cref="IGroupMembershipProvider"/> to use (e.g. "Keycloak", "Ldap", "Static").</summary>
    [Required]
    public string Provider { get; set; } = "Keycloak";

    /// <summary>Role name → group identifiers as reported by the provider (Keycloak group paths, AD DNs, ...).</summary>
    public Dictionary<string, string[]> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [Range(0, 1440)]
    public int CacheMinutes { get; set; } = 10;

    public DenyBehaviour DenyBehaviour { get; set; } = DenyBehaviour.Silent;

    /// <summary>Text sent when a denial is reported to the caller.</summary>
    public string DenyMessage { get; set; } = "Sorry, you are not authorised to use this.";

    /// <summary>Resolves the configured groups for a role, empty when unset.</summary>
    public IReadOnlyList<string> GroupsFor(Role role)
        => Roles.TryGetValue(role.ToString(), out var groups) ? groups : [];
}
