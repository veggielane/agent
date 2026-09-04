using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Mcp;

/// <summary>A definition that could not be used. <see cref="Server"/> is null when the file did not even parse.</summary>
public sealed record McpDefinitionProblem(string? Server, string? File, string Message)
{
    public override string ToString()
    {
        var where = Server ?? (File is null ? null : Path.GetFileName(File));
        return where is null ? Message : $"{where}: {Message}";
    }
}

public sealed record McpDefinitionSet(IReadOnlyList<McpServerDefinition> Servers, IReadOnlyList<McpDefinitionProblem> Problems)
{
    public static McpDefinitionSet Empty { get; } = new([], []);
}

public interface IMcpDefinitionLoader
{
    /// <summary>Never throws: unreadable files and invalid definitions become <see cref="McpDefinitionSet.Problems"/>.</summary>
    McpDefinitionSet Load();
}

/// <summary>
/// Merges <c>Mcp:Servers</c> from configuration with <c>{Directory}/*.json</c> drop-in files. A file wins over an
/// options entry of the same name. Only definitions that pass <see cref="McpServerDefinition.Validate"/> are returned.
/// </summary>
public sealed class McpDefinitionLoader : IMcpDefinitionLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IOptionsMonitor<McpOptions> _options;
    private readonly ILogger<McpDefinitionLoader> _logger;

    public McpDefinitionLoader(IOptionsMonitor<McpOptions> options, ILogger<McpDefinitionLoader> logger)
    {
        _options = options;
        _logger = logger;
    }

    public McpDefinitionSet Load() => Load(_options.CurrentValue);

    public McpDefinitionSet Load(McpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var byName = new Dictionary<string, McpServerDefinition>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<McpDefinitionProblem>();

        foreach (var (key, definition) in options.Servers ?? [])
        {
            if (definition is null)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(definition.Name) ? key : definition.Name;
            byName[name] = definition.WithName(name);
        }

        foreach (var file in EnumerateFiles(options.Directory))
        {
            var definition = ReadFile(file, problems);
            if (definition is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                definition.Name = Path.GetFileNameWithoutExtension(file);
            }

            if (byName.ContainsKey(definition.Name))
            {
                _logger.LogInformation("MCP definition file {File} overrides server '{Server}'", file, definition.Name);
            }

            byName[definition.Name] = definition;
        }

        var servers = new List<McpServerDefinition>();
        foreach (var definition in byName.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var errors = definition.Validate();
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    problems.Add(new McpDefinitionProblem(definition.Name, null, error));
                }

                continue;
            }

            servers.Add(definition);
        }

        foreach (var problem in problems)
        {
            _logger.LogWarning("MCP definition problem: {Problem}", problem);
        }

        return new McpDefinitionSet(servers, problems);
    }

    private static IEnumerable<string> EnumerateFiles(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return [];
        }

        var full = Path.GetFullPath(directory);
        return Directory.Exists(full)
            ? Directory.EnumerateFiles(full, "*.json").OrderBy(f => f, StringComparer.Ordinal)
            : [];
    }

    private McpServerDefinition? ReadFile(string file, List<McpDefinitionProblem> problems)
    {
        try
        {
            var definition = JsonSerializer.Deserialize<McpServerDefinition>(File.ReadAllText(file), JsonOptions);
            if (definition is null)
            {
                problems.Add(new McpDefinitionProblem(null, file, "File is empty or contains 'null'."));
            }

            return definition;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Skipping MCP definition file {File}", file);
            problems.Add(new McpDefinitionProblem(null, file, ex.Message));
            return null;
        }
    }
}
