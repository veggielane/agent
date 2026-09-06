using Agent.Core;
using Agent.Core.Conversations;
using Agent.Core.Events;
using Agent.Core.Replies;
using Agent.Core.Tasks;
using Agent.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agent.Channels.GitLab;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// GitLab client, read-only tools, merge request publisher and git credentials. Enough for the CLI's local
    /// mode and the worker; <see cref="AddGitLabChannel"/> adds the inbound side.
    /// </summary>
    public static IServiceCollection AddGitLabClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GitLabOptions>()
            .Bind(configuration.GetSection(GitLabOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp), "GitLab:BaseUrl must be an absolute http(s) URL.")
            .ValidateOnStart();

        services.TryAddTransient<GitLabAuthenticationHandler>();
        services.AddHttpClient<IGitLabClient, GitLabClient>((sp, http) =>
            {
                var options = sp.GetRequiredService<IOptionsMonitor<GitLabOptions>>().CurrentValue;
                http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
                http.Timeout = TimeSpan.FromSeconds(100);
            })
            .AddHttpMessageHandler<GitLabAuthenticationHandler>()
            .AddStandardResilienceHandler();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolSource, GitLabTools>());
        services.TryAddSingleton<IMergeRequestPublisher, GitLabMergeRequestPublisher>();
        services.TryAddSingleton<IRepositoryCredentialProvider, GitLabCredentialProvider>();

        // !fix lives with the client, not the poller: the CLI and every other channel start tasks with it too.
        services.AddCommandHandlers<GitLabFixCommand>();
        return services;
    }

    /// <summary>Everything from <see cref="AddGitLabClient"/> plus the poller, reply sender, history provider and task notifier.</summary>
    public static IServiceCollection AddGitLabChannel(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddGitLabClient(configuration);

        // One poller instance serves as both the hosted service and the !status event source.
        services.TryAddSingleton<GitLabPoller>();
        services.AddHostedService(sp => sp.GetRequiredService<GitLabPoller>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEventSource, GitLabPoller>(sp => sp.GetRequiredService<GitLabPoller>()));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReplySender, GitLabReplySender>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConversationContextProvider, GitLabConversationContextProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITaskNotifier, GitLabTaskNotifier>());
        return services;
    }
}

/// <summary>Adds the PRIVATE-TOKEN header per request, so a rotated token in configuration takes effect without a restart.</summary>
public sealed class GitLabAuthenticationHandler : DelegatingHandler
{
    private readonly IOptionsMonitor<GitLabOptions> _options;

    public GitLabAuthenticationHandler(IOptionsMonitor<GitLabOptions> options)
    {
        _options = options;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _options.CurrentValue.Token;
        if (!string.IsNullOrEmpty(token) && !request.Headers.Contains("PRIVATE-TOKEN"))
        {
            request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
