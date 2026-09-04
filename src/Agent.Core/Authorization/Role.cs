namespace Agent.Core.Authorization;

/// <summary>
/// Hierarchical roles. <see cref="Admin"/> implies <see cref="Team"/>, which implies <see cref="Users"/>.
/// The numeric value is the rank; 0 is deliberately unused so an unset attribute property can be detected.
/// </summary>
public enum Role
{
    Users = 1,
    Team = 2,
    Admin = 3,
}

public static class RoleExtensions
{
    /// <summary>Expands a set of directly granted roles into the full hierarchical set.</summary>
    public static IReadOnlySet<Role> Expand(this IEnumerable<Role> granted)
    {
        var max = 0;
        foreach (var role in granted)
        {
            max = Math.Max(max, (int)role);
        }

        var set = new HashSet<Role>();
        for (var r = 1; r <= max; r++)
        {
            set.Add((Role)r);
        }

        return set;
    }

    public static bool Satisfies(this IReadOnlySet<Role> roles, Role required) => roles.Contains(required);

    public static Role Highest(this IReadOnlySet<Role> roles)
        => roles.Count == 0 ? 0 : roles.Max();

    public static bool TryParseRole(string? value, out Role role)
    {
        role = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out role) && Enum.IsDefined(role) && role != 0;
    }
}
