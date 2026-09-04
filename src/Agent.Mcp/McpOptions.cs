using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Tools;

namespace Agent.Mcp;

/// <summary>The <c>Mcp</c> configuration section: global limits plus inline server definitions.</summary>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>Directory with drop-in server definitions, one JSON file per server. Relative to the working directory.</summary>
    public string Directory { get; set; } = "mcp";

    /// <summary>Tool results longer than this are truncated with a marker before the model sees them.</summary>
    public int MaxResultChars { get; set; } = 20_000;

    /// <summary>Per-call timeout for servers that do not set their own <see cref="McpServerDefinition.TimeoutSeconds"/>.</summary>
    public int DefaultTimeoutSeconds { get; set; } = 30;

    /// <summary>How often failed or dropped connections are retried.</summary>
    public int ReconnectSeconds { get; set; } = 30;

    /// <summary>Servers defined inline; the key is the server name unless the definition sets one.</summary>
    public Dictionary<string, McpServerDefinition> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum McpTransportKind
{
    Stdio,
    Http,
}

/// <summary>One MCP server: how to reach it and who may use which of its tools.</summary>
public sealed class McpServerDefinition
{
    public const string DefaultRole = "Team";

    public const ToolScope DefaultScope = ToolScope.Answer | ToolScope.Coding;

    /// <summary>Filled from the dictionary key or file name when empty.</summary>
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>"Stdio" or "Http".</summary>
    public string Transport { get; set; } = nameof(McpTransportKind.Stdio);

    /// <summary>Stdio: executable to launch.</summary>
    public string? Command { get; set; }

    /// <summary>Stdio: arguments for <see cref="Command"/>.</summary>
    public string[] Args { get; set; } = [];

    /// <summary>Stdio: environment variables for the child process (on top of a minimal PATH/HOME set).</summary>
    public Dictionary<string, string> Env { get; set; } = new();

    /// <summary>Stdio: pass the agent's whole environment to the child instead of the minimal set plus <see cref="Env"/>.</summary>
    public bool InheritEnvironment { get; set; }

    public string? WorkingDirectory { get; set; }

    /// <summary>Http: endpoint URL (Streamable HTTP, with SSE fallback).</summary>
    public string? Url { get; set; }

    /// <summary>Http: extra request headers, e.g. Authorization.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>Minimum role for every tool unless <see cref="McpToolFilter.Roles"/> overrides it. Users | Team | Admin.</summary>
    public string Role { get; set; } = DefaultRole;

    /// <summary>
    /// Which loops receive the tools: Answer, Coding. Empty = both. (Empty rather than a literal default because the
    /// configuration binder appends to pre-populated arrays, which would make narrowing impossible.)
    /// </summary>
    public string[] Scope { get; set; } = [];

    /// <summary>Channels the tools are offered on. Null or empty = every channel.</summary>
    public string[]? Channels { get; set; }

    public McpToolFilter Tools { get; set; } = new();

    /// <summary>Per-call timeout; falls back to <see cref="McpOptions.DefaultTimeoutSeconds"/>.</summary>
    public int? TimeoutSeconds { get; set; }

    public McpTransportKind ParsedTransport => TryParseTransport(Transport, out var kind) ? kind : McpTransportKind.Stdio;

    /// <summary>Falls back to Admin when the configured role is invalid, so a typo never widens access.</summary>
    public Role ParsedRole => RoleExtensions.TryParseRole(Role, out var role) ? role : Core.Authorization.Role.Admin;

    public ToolScope ParsedScope
    {
        get
        {
            var scope = ToolScope.None;
            foreach (var entry in Scope)
            {
                if (TryParseScope(entry, out var parsed))
                {
                    scope |= parsed;
                }
            }

            return scope == ToolScope.None ? DefaultScope : scope;
        }
    }

    public IReadOnlySet<Channel>? ParsedChannels
    {
        get
        {
            if (Channels is null || Channels.Length == 0)
            {
                return null;
            }

            var set = new HashSet<Channel>();
            foreach (var entry in Channels)
            {
                if (TryParseChannel(entry, out var channel))
                {
                    set.Add(channel);
                }
            }

            return set.Count == 0 ? null : set;
        }
    }

    /// <summary>Returns every problem with this definition; an empty list means it can be connected.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("Name is required.");
        }

        if (!TryParseTransport(Transport, out var transport))
        {
            problems.Add($"Unknown transport '{Transport}' (expected Stdio or Http).");
        }
        else if (transport == McpTransportKind.Stdio && string.IsNullOrWhiteSpace(Command))
        {
            problems.Add("Command is required for the Stdio transport.");
        }
        else if (transport == McpTransportKind.Http
                 && (string.IsNullOrWhiteSpace(Url)
                     || !Uri.TryCreate(Url, UriKind.Absolute, out var uri)
                     || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            problems.Add("Url must be an absolute http(s) URL for the Http transport.");
        }

        if (!RoleExtensions.TryParseRole(Role, out _))
        {
            problems.Add($"Unknown role '{Role}' (expected Users, Team, or Admin).");
        }

        foreach (var scope in Scope)
        {
            if (!TryParseScope(scope, out _))
            {
                problems.Add($"Unknown scope '{scope}' (expected Answer or Coding).");
            }
        }

        foreach (var channel in Channels ?? [])
        {
            if (!TryParseChannel(channel, out _))
            {
                problems.Add($"Unknown channel '{channel}' (expected {string.Join(", ", Enum.GetNames<Channel>())}).");
            }
        }

        foreach (var (pattern, role) in Tools.Roles)
        {
            if (!RoleExtensions.TryParseRole(role, out _))
            {
                problems.Add($"Unknown role '{role}' for tool pattern '{pattern}'.");
            }
        }

        if (TimeoutSeconds is <= 0)
        {
            problems.Add("TimeoutSeconds must be positive.");
        }

        return problems;
    }

    public static bool TryParseTransport(string? value, out McpTransportKind kind)
        => Enum.TryParse(value?.Trim(), ignoreCase: true, out kind) && Enum.IsDefined(kind);

    public static bool TryParseScope(string? value, out ToolScope scope)
    {
        scope = ToolScope.None;
        return !string.IsNullOrWhiteSpace(value)
               && Enum.TryParse(value.Trim(), ignoreCase: true, out scope)
               && scope != ToolScope.None
               && (scope & ~ToolScope.All) == 0;
    }

    public static bool TryParseChannel(string? value, out Channel channel)
    {
        channel = default;
        return !string.IsNullOrWhiteSpace(value)
               && Enum.TryParse(value.Trim(), ignoreCase: true, out channel)
               && Enum.IsDefined(channel);
    }

    internal McpServerDefinition WithName(string name)
    {
        var copy = (McpServerDefinition)MemberwiseClone();
        copy.Name = name;
        return copy;
    }
}

/// <summary>Which of a server's tools are offered, and per-tool role overrides.</summary>
public sealed class McpToolFilter
{
    /// <summary>Globs (<c>*</c>, <c>?</c>) of tools to offer. Empty = every tool.</summary>
    public string[] Allow { get; set; } = [];

    /// <summary>Globs of tools never offered; wins over <see cref="Allow"/>.</summary>
    public string[] Deny { get; set; } = [];

    /// <summary>Tool glob to role. When several patterns match one tool the highest role wins.</summary>
    public Dictionary<string, string> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAllowed(string toolName)
        => (Allow.Length == 0 || GlobMatcher.MatchesAny(Allow, toolName))
           && !GlobMatcher.MatchesAny(Deny, toolName);

    /// <summary>The overriding role for a tool, or null when no pattern matches.</summary>
    public Role? RoleFor(string toolName)
    {
        Role? best = null;
        foreach (var (pattern, value) in Roles)
        {
            if (GlobMatcher.IsMatch(pattern, toolName) && RoleExtensions.TryParseRole(value, out var role) && (best is null || role > best))
            {
                best = role;
            }
        }

        return best;
    }
}
