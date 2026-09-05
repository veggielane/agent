using Agent.Core.Agent;
using Agent.Core.Audit;
using Agent.Core.Authorization;
using Agent.Core.Commands;
using Agent.Core.Commands.BuiltIn;
using Agent.Core.Commands.Yaml;
using Agent.Core.Conversations;
using Agent.Core.Events;
using Agent.Core.Formatting;
using Agent.Core.Infrastructure;
using Agent.Core.Llm;
using Agent.Core.Pipeline;
using Agent.Core.Prompts;
using Agent.Core.Replies;
using Agent.Core.Tasks;
using Agent.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core: options, LLM factory, tools, authorization, commands, pipeline, task service and the
    /// in-memory stores. Infrastructure projects replace the stores/providers; <see cref="AddAgentCoreHostedServices"/>
    /// adds the background workers.
    /// </summary>
    public static IServiceCollection AddAgentCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LlmOptions>().Bind(configuration.GetSection(LlmOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AuthorizationOptions>().Bind(configuration.GetSection(AuthorizationOptions.SectionName)).ValidateDataAnnotations();
        services.AddOptions<CommandsOptions>().Bind(configuration.GetSection(CommandsOptions.SectionName));
        services.AddOptions<PromptOptions>().Bind(configuration.GetSection(PromptOptions.SectionName));
        services.AddOptions<PipelineOptions>().Bind(configuration.GetSection(PipelineOptions.SectionName));

        services.AddMemoryCache();
        services.AddLogging();

        // LLM
        services.TryAddSingleton<IChatClientFactory, OpenAIChatClientFactory>();
        services.TryAddSingleton<IPromptProvider, FilePromptProvider>();
        services.TryAddSingleton<IConversationSettings, InMemoryConversationSettings>();
        services.TryAddSingleton<IAgent, AgentService>();

        // Tools
        services.TryAddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IToolSource, CommandToolSource>();

        // Authorization + audit
        services.TryAddSingleton<IAuditSink, LoggingAuditSink>();
        services.TryAddSingleton<IRoleResolver, RoleResolver>();
        services.TryAddSingleton<IAuthorizationService, AuthorizationService>();
        var staticGroups = configuration.GetSection("Authorization:StaticGroups").Get<Dictionary<string, string[]>>();
        services.AddSingleton<IGroupMembershipProvider>(new StaticGroupMembershipProvider(staticGroups));

        // Commands
        services.TryAddSingleton<ICommandRegistry, CommandRegistry>();
        services.AddSingleton<IReloadable>(sp => (CommandRegistry)sp.GetRequiredService<ICommandRegistry>());
        services.TryAddSingleton<ICommandDispatcher, CommandDispatcher>();
        services.AddSingleton<ICommandSource, AttributedCommandSource>();
        services.AddSingleton<ICommandSource, ClassCommandSource>();
        services.AddSingleton<YamlCommandSource>();
        services.AddSingleton<ICommandSource>(sp => sp.GetRequiredService<YamlCommandSource>());
        services.AddCommandHandlers<BuiltInCommands>();

        // Conversations, replies, formatting
        services.TryAddSingleton<IConversationContextRouter, ConversationContextRouter>();
        services.TryAddSingleton<IFormatterRegistry, FormatterRegistry>();
        services.TryAddSingleton<IReplyRouter, ReplyRouter>();

        // Tasks
        services.TryAddSingleton<ITaskStore, InMemoryTaskStore>();
        services.TryAddSingleton<ITaskQueueSignal, TaskQueueSignal>();
        services.TryAddSingleton<ITaskCancellationRegistry, TaskCancellationRegistry>();
        services.TryAddSingleton<ITaskService, TaskService>();
        services.TryAddSingleton<ITaskNotifierRouter, TaskNotifierRouter>();

        // Pipeline
        services.TryAddSingleton<IProcessedEventStore, InMemoryProcessedEventStore>();
        services.TryAddSingleton<ICursorStore, InMemoryCursorStore>();
        services.TryAddSingleton<IInboundQueue>(_ =>
        {
            var queue = new InboundQueue();
            Observability.AgentTelemetry.TrackQueueDepth(() => queue.Depth);
            return queue;
        });
        services.TryAddSingleton<IInboundProcessor, InboundProcessor>();

        return services;
    }

    /// <summary>Background services: inbound worker and command file watcher. Not used by the CLI's local mode.</summary>
    public static IServiceCollection AddAgentCoreHostedServices(this IServiceCollection services)
    {
        services.AddHostedService<InboundWorker>();
        services.AddHostedService<CommandFileWatcher>();
        return services;
    }

    /// <summary>Registers a type whose <c>[Command]</c> methods become commands. The type is created through DI per invocation.</summary>
    public static IServiceCollection AddCommandHandlers<T>(this IServiceCollection services)
        where T : class
    {
        services.TryAddTransient<T>();
        services.AddSingleton(new CommandHandlerRegistration(typeof(T)));
        return services;
    }

    public static IServiceCollection AddCommand<T>(this IServiceCollection services)
        where T : class, ICommand
    {
        services.AddSingleton<ICommand, T>();
        return services;
    }

    /// <summary>Registers a tool source (native tool class or MCP provider).</summary>
    public static IServiceCollection AddToolSource<T>(this IServiceCollection services)
        where T : class, IToolSource
    {
        services.AddSingleton<IToolSource, T>();
        return services;
    }
}
