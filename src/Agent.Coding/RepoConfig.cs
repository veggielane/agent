using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Agent.Coding;

/// <summary>What the repository tells us about itself: build/test/lint commands, extra policy, and guidance for the model.</summary>
public sealed record RepoProfile(
    string? BuildCommand,
    string? TestCommand,
    string? LintCommand,
    IReadOnlyList<string> ExtraAllowedExecutables,
    IReadOnlyList<string> ExtraProtectedPaths,
    string? Instructions)
{
    public static RepoProfile Empty { get; } = new(null, null, null, [], [], null);

    public bool HasVerification => !string.IsNullOrWhiteSpace(BuildCommand) || !string.IsNullOrWhiteSpace(TestCommand);
}

/// <summary>
/// Reads <c>.agent/config.yml</c> and <c>AGENTS.md</c> from the repo root, filling gaps by detecting the toolchain.
/// Everything read here is repository content and therefore untrusted: it only ever narrows policy (extra
/// protected paths) or adds allow-listed executables that the global policy still has to accept.
/// </summary>
public static class RepoConfigLoader
{
    public const string ConfigPath = ".agent/config.yml";
    public const string InstructionsFile = "AGENTS.md";

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static RepoProfile Load(string repoRoot)
    {
        var configured = RepoProfile.Empty;
        var configFile = Path.Combine(repoRoot, ConfigPath);
        if (File.Exists(configFile))
        {
            configured = ParseYaml(File.ReadAllText(configFile));
        }

        var detected = Detect(repoRoot);

        string? instructions = null;
        var agentsFile = Path.Combine(repoRoot, InstructionsFile);
        if (File.Exists(agentsFile))
        {
            instructions = File.ReadAllText(agentsFile).Trim();
        }

        if (!string.IsNullOrWhiteSpace(configured.Instructions))
        {
            instructions = string.IsNullOrEmpty(instructions)
                ? configured.Instructions.Trim()
                : instructions + "\n\n" + configured.Instructions.Trim();
        }

        return new RepoProfile(
            Pick(configured.BuildCommand, detected.BuildCommand),
            Pick(configured.TestCommand, detected.TestCommand),
            Pick(configured.LintCommand, detected.LintCommand),
            configured.ExtraAllowedExecutables,
            configured.ExtraProtectedPaths,
            string.IsNullOrWhiteSpace(instructions) ? null : instructions);
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
            Clean(doc.Instructions));
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

        public string? Instructions { get; set; }
    }
}
