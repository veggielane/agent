using System.Security.Claims;
using System.Text.Json;

namespace Agent.Infrastructure.Keycloak;

/// <summary>
/// Reads group identifiers out of a Keycloak token: the <see cref="KeycloakOptions.GroupClaim"/> array plus the
/// roles at <see cref="KeycloakOptions.RolesClaimPath"/> as <c>role:{name}</c>. The host maps the result through
/// <c>IRoleResolver.RolesFromGroups</c>.
/// </summary>
public static class ClaimsGroupExtractor
{
    public const string RolePrefix = "role:";

    public static IReadOnlyCollection<string> Extract(ClaimsPrincipal principal, KeycloakOptions options)
    {
        var collector = new Collector();

        var groupPath = Split(options.GroupClaim);
        if (groupPath.Length > 0)
        {
            foreach (var claim in principal.FindAll(groupPath[0]))
            {
                AddClaimValue(collector, claim.Value, groupPath.AsSpan(1), prefix: null);
            }
        }

        var rolePath = Split(options.RolesClaimPath);
        if (rolePath.Length > 0)
        {
            foreach (var claim in principal.FindAll(rolePath[0]))
            {
                AddClaimValue(collector, claim.Value, rolePath.AsSpan(1), RolePrefix);
            }
        }

        return collector.Values;
    }

    public static IReadOnlyCollection<string> Extract(JsonElement payload, KeycloakOptions options)
    {
        var collector = new Collector();

        if (TryNavigate(payload, Split(options.GroupClaim), out var groups))
        {
            AddElement(collector, groups, prefix: null);
        }

        if (TryNavigate(payload, Split(options.RolesClaimPath), out var roles))
        {
            AddElement(collector, roles, RolePrefix);
        }

        return collector.Values;
    }

    private static string[] Split(string? path)
        => string.IsNullOrWhiteSpace(path) ? [] : path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// A claim value is either a plain string (one group per claim), a JSON array (some handlers keep arrays
    /// intact) or a JSON object to descend into for nested paths such as <c>realm_access.roles</c>.
    /// </summary>
    private static void AddClaimValue(Collector collector, string value, ReadOnlySpan<string> remainingPath, string? prefix)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (TryNavigate(document.RootElement, remainingPath, out var element))
                {
                    AddElement(collector, element, prefix);
                }

                return;
            }
            catch (JsonException)
            {
                // Not JSON after all: treat as a literal value below.
            }
        }

        if (remainingPath.Length == 0)
        {
            collector.Add(trimmed, prefix);
        }
    }

    private static bool TryNavigate(JsonElement element, ReadOnlySpan<string> path, out JsonElement result)
    {
        result = element;
        foreach (var segment in path)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(segment, out result))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddElement(Collector collector, JsonElement element, string? prefix)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        collector.Add(item.GetString(), prefix);
                    }
                }

                break;
            case JsonValueKind.String:
                collector.Add(element.GetString(), prefix);
                break;
        }
    }

    private sealed class Collector
    {
        private readonly List<string> _values = [];
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> Values => _values;

        public void Add(string? value, string? prefix)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var full = prefix is null ? value.Trim() : prefix + value.Trim();
            if (_seen.Add(full))
            {
                _values.Add(full);
            }
        }
    }
}
