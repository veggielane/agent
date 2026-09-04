namespace Agent.Coding.Sandbox;

public enum SandboxMode
{
    /// <summary>Commands run as child processes on the worker host with a scrubbed environment.</summary>
    Process,

    /// <summary>One Docker container per task; commands run inside it with the repository bind-mounted.</summary>
    Docker,
}

/// <summary>
/// Bound from <c>Coding:Sandbox</c>. Only the model's commands (the <c>run</c> tool and build/test verification)
/// go through the sandbox. Git, credentials, and file edits stay with the orchestrator on the host, so the
/// container never sees a token.
/// </summary>
public sealed class SandboxOptions
{
    public SandboxMode Mode { get; set; } = SandboxMode.Process;

    /// <summary>Executable used to talk to the daemon. Full path or a name on PATH.</summary>
    public string DockerPath { get; set; } = "docker";

    /// <summary>Image when no toolchain-specific image applies.</summary>
    public string DefaultImage { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";

    /// <summary>Toolchain (detected from the repository's build/test commands) → image. Configured keys override the defaults.</summary>
    public Dictionary<string, string> Images { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dotnet"] = "mcr.microsoft.com/dotnet/sdk:10.0",
        ["node"] = "node:22",
        ["python"] = "python:3.12",
        ["go"] = "golang:1.23",
        ["rust"] = "rust:1",
    };

    /// <summary>
    /// The menu a repository may choose from by name in <c>.engex.yml</c> (<c>container: {profile: ...}</c>).
    /// This is the safe way to let repositories pick an image: they can only select what you defined here.
    /// </summary>
    public Dictionary<string, SandboxProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Glob patterns for images a repository may name directly (<c>container: {image: ...}</c>), e.g.
    /// <c>our-registry.corp/*</c>. Empty (the default) means repositories may not name images at all;
    /// an image is arbitrary code, so opening this is a deliberate decision.
    /// </summary>
    public List<string> AllowedImages { get; set; } = [];

    /// <summary>Ceiling for a repository's memory request. Empty means no ceiling beyond <see cref="Memory"/>.</summary>
    public string? MaxMemory { get; set; } = "8g";

    /// <summary>Ceiling for a repository's CPU request. 0 means no ceiling.</summary>
    public double MaxCpus { get; set; } = 4;

    /// <summary>
    /// Docker network for the container: <c>none</c> blocks all egress (restores must then hit a pre-populated
    /// cache volume), <c>bridge</c> allows it, or the name of a network whose egress is restricted to GitLab and
    /// the package registries.
    /// </summary>
    public string Network { get; set; } = "bridge";

    public string Memory { get; set; } = "4g";

    public double Cpus { get; set; } = 2;

    public int PidsLimit { get; set; } = 512;

    /// <summary>Run as this user inside the container (e.g. "1000:1000"). Null keeps the image default.</summary>
    public string? User { get; set; }

    /// <summary>Mount point of the repository inside the container.</summary>
    public string WorkDir { get; set; } = "/work";

    public bool ReadOnlyRootFilesystem { get; set; }

    /// <summary>Size of the writable <c>/tmp</c> tmpfs; empty disables the tmpfs mount.</summary>
    public string TmpfsSize { get; set; } = "1g";

    /// <summary>Adds <c>--init</c> so a tiny init reaps zombie processes.</summary>
    public bool Init { get; set; } = true;

    /// <summary>Extra mounts in <c>docker -v</c> syntax, e.g. a shared NuGet cache volume: <c>agent-nuget:/root/.nuget/packages</c>.</summary>
    public List<string> Volumes { get; set; } = [];

    /// <summary>Environment inside the container (not secrets: values are visible in the docker command line).</summary>
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raw extra arguments for <c>docker run</c> (advanced: seccomp profiles, ulimits, DNS).</summary>
    public List<string> ExtraArgs { get; set; } = [];

    /// <summary>Budget for <c>docker run</c>, including an image pull on first use.</summary>
    public int StartTimeoutSeconds { get; set; } = 300;

    /// <summary>Wrap every command in coreutils <c>timeout -s KILL</c> inside the container so a hung process dies there, not just the client.</summary>
    public bool UseTimeoutWrapper { get; set; } = true;
}
