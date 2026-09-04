using Agent.Core.Conversations;
using Agent.Core.Events;
using Agent.Core.Replies;
using Agent.Core.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Mattermost;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The full channel: options, REST client, WebSocket listener (hosted service and <see cref="IEventSource"/>),
    /// reply sender, conversation context provider and task notifier. Call after <c>AddAgentCore</c>.
    /// </summary>
    public static IServiceCollection AddMattermostChannel(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMattermostClient(configuration);

        services.TryAddSingleton<IMattermostSocketFactory, ClientWebSocketMattermostSocketFactory>();
        services.TryAddSingleton<MattermostEventMapper>();

        services.TryAddSingleton<MattermostListener>();
        services.AddSingleton<IEventSource>(sp => sp.GetRequiredService<MattermostListener>());
        services.AddHostedService(sp => sp.GetRequiredService<MattermostListener>());

        services.AddSingleton<IReplySender, MattermostReplySender>();
        services.AddSingleton<IConversationContextProvider, MattermostConversationContextProvider>();
        services.AddSingleton<ITaskNotifier, MattermostTaskNotifier>();

        return services;
    }

    /// <summary>Options and the REST client only, for the CLI and for tools that post to Mattermost.</summary>
    public static IServiceCollection AddMattermostClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MattermostOptions>()
            .Bind(configuration.GetSection(MattermostOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.BaseUrl), "Mattermost:BaseUrl is required.")
            .Validate(o => Uri.TryCreate(o.BaseUrl.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps), "Mattermost:BaseUrl must be an absolute http(s) URL.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.BotToken), "Mattermost:BotToken is required.")
            .Validate(o => o.MaxPostLength >= 64, "Mattermost:MaxPostLength must be at least 64.")
            .ValidateOnStart();

        services.AddMemoryCache();
        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient<IMattermostClient, MattermostClient>((sp, http) =>
            {
                var options = sp.GetRequiredService<IOptionsMonitor<MattermostOptions>>().CurrentValue;
                if (!string.IsNullOrWhiteSpace(options.BaseUrl))
                {
                    http.BaseAddress = MattermostClient.NormalizeBaseUrl(options.BaseUrl);
                }
            })
            .AddStandardResilienceHandler(options => options.Retry.DisableForUnsafeHttpMethods());

        services.TryAddSingleton<IMattermostUserDirectory, MattermostUserDirectory>();

        return services;
    }
}
