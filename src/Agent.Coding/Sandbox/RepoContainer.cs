namespace Agent.Coding.Sandbox;

/// <summary>
/// What a repository asks for in the <c>container:</c> block of <c>.engex.yml</c>. This is repository
/// content and therefore untrusted: it is a <em>request</em>, and <see cref="SandboxPolicy"/> decides what
/// the host actually grants. Nothing here can widen the host's policy.
/// </summary>
public sealed record RepoContainer(
    string? Profile = null,
    string? Image = null,
    string? Memory = null,
    double? Cpus = null,
    string? Network = null,
    IReadOnlyDictionary<string, string>? Env = null)
{
    public static RepoContainer Empty { get; } = new();

    public bool IsEmpty => string.IsNullOrWhiteSpace(Profile)
        && string.IsNullOrWhiteSpace(Image)
        && string.IsNullOrWhiteSpace(Memory)
        && Cpus is null
        && string.IsNullOrWhiteSpace(Network)
        && (Env is null || Env.Count == 0);
}

/// <summary>One entry on the host's menu of images a repository may choose by name.</summary>
public sealed class SandboxProfile
{
    /// <summary>Container image. Required.</summary>
    public string Image { get; set; } = string.Empty;

    public string? Memory { get; set; }

    public double? Cpus { get; set; }

    public string? Network { get; set; }

    /// <summary>Extra mounts for this profile, e.g. a toolchain-specific package cache.</summary>
    public List<string> Volumes { get; set; } = [];

    public Dictionary<string, string> Env { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Description { get; set; }
}

/// <summary>The container settings for one run, after the host has vetted the repository's request.</summary>
public sealed record ResolvedSandbox(
    string Image,
    string Network,
    string Memory,
    double Cpus,
    IReadOnlyList<string> Volumes,
    IReadOnlyDictionary<string, string> Env,
    string Source);
