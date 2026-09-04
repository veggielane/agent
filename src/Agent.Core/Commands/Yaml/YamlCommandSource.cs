using System.Text.RegularExpressions;
using Agent.Core.Agent;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Conversations;
using Agent.Core.Llm;
using Agent.Core.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Agent.Core.Commands.Yaml;

/// <summary>Loads prompt-template commands from <c>commands/*.yaml</c> and rebuilds the registry when files change.</summary>
public sealed partial class YamlCommandSource : ICommandSource, IDisposable
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly IOptionsMonitor<CommandsOptions> _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<YamlCommandSource> _logger;
    private readonly List<string> _problems = [];
    private PhysicalFileProvider? _fileProvider;
    private IDisposable? _watch;
    private Action? _onChanged;

    public YamlCommandSource(IOptionsMonitor<CommandsOptions> options, IServiceProvider services, ILogger<YamlCommandSource> logger)
    {
        _options = options;
        _services = services;
        _logger = logger;
    }

    public IReadOnlyList<string> Problems => _problems;

    /// <summary>Starts watching the commands directory; <paramref name="onChanged"/> runs after any file change.</summary>
    public void Watch(Action onChanged)
    {
        _onChanged = onChanged;
        var dir = Path.GetFullPath(_options.CurrentValue.Directory);
        if (!Directory.Exists(dir))
        {
            _logger.LogInformation("Commands directory {Dir} does not exist; YAML commands disabled until it appears", dir);
            return;
        }

        _fileProvider = new PhysicalFileProvider(dir);
        RegisterWatch();
    }

    private void RegisterWatch()
    {
        if (_fileProvider is null)
        {
            return;
        }

        var token = _fileProvider.Watch("*.y*ml");
        _watch = ChangeToken.OnChange(() => _fileProvider.Watch("*.y*ml"), () =>
        {
            _logger.LogInformation("Command files changed; reloading");
            _onChanged?.Invoke();
        });
        _ = token;
    }

    public IEnumerable<CommandDescriptor> GetCommands()
    {
        _problems.Clear();
        var dir = _options.CurrentValue.Directory;
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.y*ml").OrderBy(f => f, StringComparer.Ordinal))
        {
            CommandDescriptor? descriptor = null;
            try
            {
                var definition = Deserializer.Deserialize<PromptCommandDefinition>(File.ReadAllText(file));
                descriptor = Build(definition, Path.GetFileName(file));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                _logger.LogWarning(ex, "Skipping command file {File}", file);
            }

            if (descriptor is not null)
            {
                yield return descriptor;
            }
        }
    }

    public CommandDescriptor Build(PromptCommandDefinition def, string fileName)
    {
        if (string.IsNullOrWhiteSpace(def.Name))
        {
            throw new CommandDefinitionException("'name' is required.");
        }

        if (string.IsNullOrWhiteSpace(def.Prompt))
        {
            throw new CommandDefinitionException("'prompt' is required.");
        }

        if (!RoleExtensions.TryParseRole(def.Role, out var role))
        {
            throw new CommandDefinitionException("'role' is required and must be users, team or admin.");
        }

        var kind = (def.Kind ?? "ask").Trim().ToLowerInvariant();
        if (kind is not ("ask" or "task"))
        {
            throw new CommandDefinitionException("'kind' must be ask or task.");
        }

        HashSet<Channel>? channels = null;
        if (def.Channels is { Count: > 0 })
        {
            channels = [];
            foreach (var c in def.Channels)
            {
                if (!Enum.TryParse<Channel>(c, ignoreCase: true, out var channel))
                {
                    throw new CommandDefinitionException($"Unknown channel '{c}'.");
                }

                channels.Add(channel);
            }
        }

        var unknownPlaceholder = PlaceholderRegex().Matches(def.Prompt)
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault(p => !KnownPlaceholders.Contains(p));
        if (unknownPlaceholder is not null)
        {
            throw new CommandDefinitionException($"Unknown placeholder {{{{{unknownPlaceholder}}}}}.");
        }

        var parameters = new[] { new CommandParameter("text", typeof(string), false, true, false, string.Empty, "Extra instructions") };
        var services = _services;
        var options = _options;

        return new CommandDescriptor(
            def.Name,
            def.Description ?? def.Name,
            role,
            (ctx, args) => ExecuteAsync(def, kind, ctx, args, services, options.CurrentValue),
            parameters,
            def.Aliases,
            channels,
            def.Hidden,
            exposeAsTool: false,
            source: fileName);
    }

    private static async Task<CommandResult> ExecuteAsync(PromptCommandDefinition def, string kind, CommandContext ctx, ParsedArgs args, IServiceProvider services, CommandsOptions options)
    {
        var contexts = services.GetRequiredService<IConversationContextRouter>();
        var historyText = ctx.Event is null ? string.Empty : await contexts.GetHistoryTextAsync(ctx.Event, options.ContextMessages, ctx.CancellationToken).ConfigureAwait(false);

        var task = ctx.Event?.Task;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["args"] = args.Rest,
            ["context"] = historyText,
            ["caller"] = ctx.Caller.DisplayName,
            ["issue"] = task?.SourceRef ?? ctx.Event?.Meta("issue") ?? string.Empty,
            ["repo"] = task?.RepoUrl ?? ctx.Event?.Meta("repo") ?? string.Empty,
            ["branch"] = task?.ExistingBranch ?? task?.BaseBranch ?? ctx.Event?.Meta("branch") ?? string.Empty,
            ["channel"] = ctx.Channel.ToString(),
        };

        var rendered = PlaceholderRegex().Replace(def.Prompt!, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value).Trim();

        if (kind == "task")
        {
            if (ctx.Event?.Task is null)
            {
                return CommandResult.Error("This command needs an issue or merge request context to start a task.");
            }

            var tasks = services.GetRequiredService<ITaskService>();
            var request = TaskRequest.From(ctx.Event with { Text = rendered }, ctx.Caller);
            var created = await tasks.CreateAsync(request, ctx.CancellationToken).ConfigureAwait(false);
            return CommandResult.Text(created.Status == AgentTaskStatus.NeedsInput
                ? $"Created task #{created.Id}, but no repository could be resolved. Tell me which repo to use."
                : $"Picked up as task #{created.Id}. I'll report back here when the merge request is ready.");
        }

        var agent = services.GetRequiredService<IAgent>();
        IReadOnlyCollection<string>? toolFilter = def.Tools is { Count: > 0 } ? def.Tools : null;
        var disableTools = def.Tools is { Count: 1 } && string.Equals(def.Tools[0], "none", StringComparison.OrdinalIgnoreCase);

        var response = await agent.AnswerAsync(new AgentRequest
        {
            Caller = ctx.Caller,
            Channel = ctx.Channel,
            ConversationId = ctx.ConversationId,
            Text = rendered,
            ModelOverride = ResolveModel(def.Model, services),
            ToolFilter = disableTools ? null : toolFilter,
            DisableTools = disableTools,
            ExtraInstructions = def.System,
        }, ctx.CancellationToken).ConfigureAwait(false);

        return CommandResult.Text(response.Markdown);
    }

    private static string? ResolveModel(string? model, IServiceProvider services)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var factory = services.GetRequiredService<IChatClientFactory>();
        return model.Trim() switch
        {
            "AnswerModel" => factory.ResolveModel(ModelPurpose.Answer),
            "CodingModel" => factory.ResolveModel(ModelPurpose.Coding),
            var m => m,
        };
    }

    private static readonly HashSet<string> KnownPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "args", "context", "caller", "issue", "repo", "branch", "channel",
    };

    [GeneratedRegex(@"\{\{\s*([a-zA-Z_]+)\s*\}\}")]
    private static partial Regex PlaceholderRegex();

    public void Dispose()
    {
        _watch?.Dispose();
        _fileProvider?.Dispose();
    }
}
