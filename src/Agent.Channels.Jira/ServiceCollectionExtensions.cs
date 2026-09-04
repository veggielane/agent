using System.Net;
using Agent.Core.Conversations;
using Agent.Core.Events;
using Agent.Core.Formatting;
using Agent.Core.Replies;
using Agent.Core.Tasks;
using Agent.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Jira;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Jira options, the typed <see cref="IJiraClient"/> (bearer/basic auth, standard resilience), the read-only tools,
    /// the wiki-markup formatter and the repository resolver. Enough for the CLI; no polling.
    /// </summary>
    public static IServiceCollection AddJiraClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JiraOptions>()
            .Bind(configuration.GetSection(JiraOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<IJiraClient, JiraClient>((sp, client) =>
            {
                // The client builds absolute URIs and sets Authorization per request from IOptionsMonitor, so config
                // edits apply without recreating the HttpClient; these defaults only cover direct HttpClient use.
                var options = sp.GetRequiredService<IOptionsMonitor<JiraOptions>>().CurrentValue;
                if (Uri.TryCreate(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
                {
                    client.BaseAddress = baseUri;
                }

                client.DefaultRequestHeaders.Authorization = JiraClient.CreateAuthorization(options);
                client.Timeout = TimeSpan.FromSeconds(120);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                AutomaticDecompression = DecompressionMethods.All,
            })
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            });

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolSource, JiraTools>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IResponseFormatter, JiraWikiFormatter>());
        services.TryAddSingleton<IRepositoryResolver, JiraRepositoryResolver>();
        return services;
    }

    /// <summary>Everything in <see cref="AddJiraClient"/> plus the poller, reply sender, conversation history and task notifier.</summary>
    public static IServiceCollection AddJiraChannel(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddJiraClient(configuration);

        services.TryAddSingleton<JiraPoller>();
        services.AddSingleton<IEventSource>(sp => sp.GetRequiredService<JiraPoller>());
        services.AddHostedService(sp => sp.GetRequiredService<JiraPoller>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReplySender, JiraReplySender>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConversationContextProvider, JiraConversationContextProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITaskNotifier, JiraTaskNotifier>());
        return services;
    }
}
