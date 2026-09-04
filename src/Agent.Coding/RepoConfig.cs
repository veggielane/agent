using Agent.Coding.Sandbox;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Agent.Coding;

/// <summary>What the repository tells us about itself: build/test/lint commands, extra policy, the container it wants, and guidance for the model.</summary>
public sealed record RepoProfile(
    string? BuildCommand,
    string? TestCommand,
    string? LintCommand,
    IReadOnlyList<string> ExtraAllowedExecutables,
    IReadOnlyList<string> ExtraProtectedPaths,
    string? Instructions,
    RepoContainer? Container = null)
{
    public static RepoProfile Empty { get; } = new(null, null, null, [], [], null);

    public bool HasVerification => !string.IsNullOrWhiteSpace(BuildCommand) || !string.IsNullOrWhiteSpace(TestCommand);
}

/// <summary>
/// Reads <c>.engex.yml</c> (structured settings) and <c>AGENTS.md</c> (standing guidance) from the repo root,
/// filling gaps by detecting the toolchain. Everything read here is repository content and therefore
/// untrusted: it only ever narrows policy, adds executables the global allow list still has to accept, or
/// supplies context. What to actually do comes from the requester, never from the repository.
/// </summary>
public static class RepoConfigLoader
{
    /// <summary>Repository configuration file, in order of preference. The first one present wins.</summary>
    public static readonly string[] ConfigPaths = [".engex.yml", ".engex.yaml", ".agent/config.yml", ".agent/config.yaml"];

    /// <summary>The file teams are told to write. The others are accepted for compatibility.</summary>
    public const string ConfigPath = ".engex.yml";

    public const string InstructionsFile = "AGENTS.md";

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static RepoProfile Load(string repoRoot)
    {
        var configured = RepoProfile.Empty;
        foreach (var candidate in ConfigPaths)
        {
            var configFile = Path.Combine(repoRoot, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(configFile))
            {
                configured = ParseYaml(File.ReadAllText(configFile));
                break;
            }
        }

        var detected = Detect(repoRoot);

        // Standing repository guidance lives in AGENTS.md and nowhere else: the config file carries
        // structured settings, and the task itself comes from the requester, not from the repository.
        string? instructions = null;
        var agentsFile = Path.Combine(repoRoot, InstructionsFile);
        if (File.Exists(agentsFile))
        {
            instructions = File.ReadAllText(agentsFile).Trim();
        }

        return new RepoProfile(
            Pick(configured.BuildCommand, detected.BuildCommand),
            Pick(configured.TestCommand, detected.TestCommand),
            Pick(configured.LintCommand, detected.LintCommand),
            configured.ExtraAllowedExecutables,
            configured.ExtraProtectedPaths,
            string.IsNullOrWhiteSpace(instructions) ? null : instructions,
            configured.Container);
    }

    /// <summary>Parses the YAML document; unknown keys are ignored, malformed YAML yields the empty profile.</summary>
    public static RepoProfile ParseYaml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return RepoProfile.Empty;
        }

        ConfigDocument? doc;
        try
        {
            doc = Yaml.Deserialize<ConfigDocument>(yaml);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return RepoProfile.Empty;
        }

        if (doc is null)
        {
            return RepoProfile.Empty;
        }

        return new RepoProfile(
            Clean(doc.Build),
            Clean(doc.Test),
            Clean(doc.Lint),
            CleanList(doc.AllowedExecutables),
            CleanList(doc.ProtectedPaths),
            Instructions: null,
            ToContainer(doc.Container));
    }

    private static RepoContainer? ToContainer(ContainerDocument? doc)
    {
        if (doc is null)
        {
            return null;
        }

        Dictionary<string, string>? env = null;
        if (doc.Env is { Count: > 0 })
        {
            env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in doc.Env)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    env[key.Trim()] = value ?? string.Empty;
                }
            }
        }

        var container = new RepoContainer(Clean(doc.Profile), Clean(doc.Image), Clean(doc.Memory), doc.Cpus, Clean(doc.Network), env);
        return container.IsEmpty ? null : container;
    }

    /// <summary>Guesses build and test commands from well-known files at the repo root.</summary>
    public static RepoProfile Detect(string repoRoot)
    {
        if (!Directory.Exists(repoRoot))
        {
            return RepoProfile.Empty;
        }

        if (HasFile(repoRoot, "*.sln") || HasFile(repoRoot, "*.slnx") || HasFile(repoRoot, "*.csproj"))
        {
            return RepoProfile.Empty with { BuildCommand = "dotnet build", TestCommand = "dotnet test --no-build" };
        }

        if (File.Exists(Path.Combine(repoRoot, "package.json")))
        {
            return RepoProfile.Empty with { BuildCommand = "npm ci", TestCommand = "npm test" };
        }

        if (File.Exists(Path.Combine(repoRoot, "Makefile")) || File.Exists(Path.Combine(repoRoot, "makefile")))
        {
            return RepoProfile.Empty with { BuildCommand = "make", TestCommand = "make test" };
        }

        if (File.Exists(Path.Combine(repoRoot, "pyproject.toml")))
        {
            return RepoProfile.Empty with { TestCommand = "pytest" };
        }

        return RepoProfile.Empty;
    }

    private static bool HasFile(string dir, string pattern)
        => Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).Any();

    private static string? Pick(string? configured, string? detected)
        => string.IsNullOrWhiteSpace(configured) ? detected : configured;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IReadOnlyList<string> CleanList(List<string>? items)
        => items is null ? [] : items.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToList();

    private sealed class ConfigDocument
    {
        public string? Build { get; set; }

        public string? Test { get; set; }

        public string? Lint { get; set; }

        public List<string>? AllowedExecutables { get; set; }

        public List<string>? ProtectedPaths { get; set; }

        public ContainerDocument? Container { get; set; }
    }

    private sealed class ContainerDocument
    {
        public string? Profile { get; set; }

        public string? Image { get; set; }

        public string? Memory { get; set; }

        public double? Cpus { get; set; }

        public string? Network { get; set; }

        public Dictionary<string, string>? Env { get; set; }
    }
}
