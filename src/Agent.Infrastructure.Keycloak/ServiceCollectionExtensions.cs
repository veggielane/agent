using Agent.Core.Authorization;
using Agent.Infrastructure.Keycloak.DeviceFlow;
using Agent.Infrastructure.Keycloak.TokenCache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Agent.Infrastructure.Keycloak;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Group lookups through the Keycloak Admin API for callers that never present a token (Mattermost, Jira,
    /// GitLab). Registers <see cref="KeycloakGroupMembershipProvider"/> as an <see cref="IGroupMembershipProvider"/>
    /// named "Keycloak"; select it with <c>Authorization:Provider</c>.
    /// </summary>
    public static IServiceCollection AddKeycloakAuthorization(this IServiceCollection services, IConfiguration configuration)
    {
        AddOptions(services, configuration);

        services.AddHttpClient<KeycloakGroupMembershipProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            .AddStandardResilienceHandler()
            .Configure(ConfigureResilience);

        services.AddSingleton<IGroupMembershipProvider>(sp => sp.GetRequiredService<KeycloakGroupMembershipProvider>());
        return services;
    }

    /// <summary>Device-flow login and the per-user token cache for the CLI.</summary>
    public static IServiceCollection AddKeycloakCli(this IServiceCollection services, IConfiguration configuration)
    {
        AddOptions(services, configuration);

        services.AddHttpClient<IKeycloakDeviceFlowClient, KeycloakDeviceFlowClient>()
            .AddStandardResilienceHandler()
            .Configure(ConfigureResilience);

        services.TryAddSingleton<ITokenCache>(_ => new FileTokenCache());
        return services;
    }

    private static void AddOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<KeycloakOptions>()
            .Bind(configuration.GetSection(KeycloakOptions.SectionName))
            .ValidateDataAnnotations();
        services.TryAddSingleton(TimeProvider.System);
    }

    private static void ConfigureResilience(HttpStandardResilienceOptions options, IServiceProvider provider)
    {
        var timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptionsMonitor<KeycloakOptions>>().CurrentValue.TimeoutSeconds);
        options.AttemptTimeout.Timeout = timeout;
        options.TotalRequestTimeout.Timeout = timeout * 3;
        options.CircuitBreaker.SamplingDuration = timeout * 2;
    }
}
