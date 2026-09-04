using System.Security.Claims;
using System.Text.Json;
using Agent.Core.Authorization;
using Agent.Core.Channels;

namespace Agent.Host.Api;

/// <summary>Builds the <see cref="CallerIdentity"/> for an API request from the validated Keycloak token.</summary>
public static class ApiCaller
{
    public static CallerIdentity From(ClaimsPrincipal principal, IRoleResolver roles, string groupClaim = "groups")
    {
        var sub = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.Identity?.Name ?? "unknown";
        var username = principal.FindFirst("preferred_username")?.Value ?? principal.Identity?.Name;
        var email = principal.FindFirst("email")?.Value ?? principal.FindFirst(ClaimTypes.Email)?.Value;

        var groups = ExtractGroups(principal, groupClaim);
        return new CallerIdentity(Channel.Cli, sub, username, email)
        {
            Groups = groups,
            Roles = roles.RolesFromGroups(groups),
            RolesResolved = true,
        };
    }

    public static IReadOnlyCollection<string> ExtractGroups(ClaimsPrincipal principal, string groupClaim)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var claim in principal.FindAll(groupClaim))
        {
            AddClaimValue(groups, claim.Value);
        }

        foreach (var claim in principal.FindAll("realm_access"))
        {
            try
            {
                using var doc = JsonDocument.Parse(claim.Value);
                if (doc.RootElement.TryGetProperty("roles", out var rolesEl) && rolesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in rolesEl.EnumerateArray())
                    {
                        if (r.GetString() is { } role)
                        {
                            groups.Add("role:" + role);
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        return groups;
    }

    private static void AddClaimValue(ISet<string> groups, string value)
    {
        if (value.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(value);
                foreach (var g in doc.RootElement.EnumerateArray())
                {
                    if (g.GetString() is { } s)
                    {
                        groups.Add(s);
                    }
                }

                return;
            }
            catch (JsonException)
            {
            }
        }

        groups.Add(value);
    }
}
