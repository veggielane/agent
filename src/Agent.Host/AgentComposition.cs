using Agent.Channels.GitLab;
using Agent.Channels.Jira;
using Agent.Channels.Mattermost;
using Agent.Coding;
using Agent.Core;
using Agent.Infrastructure.Keycloak;
using Agent.Infrastructure.Ldap;
using Agent.Mcp;
using Agent.Persistence;
using Agent.Worker;

namespace Agent.Host;

/// <summary>The one place that decides which parts of the agent run in this process.</summary>
public static class AgentComposition
{
    public static IServiceCollection AddAgent(this IServiceCollection services, IConfiguration configuration, ILogger? logger = null)
    {
        services.AddAgentCore(configuration);

        // Durable state first: hosted services start in registration order and the migration must run
        // before any worker touches the database.
        if (!string.IsNullOrWhiteSpace(configuration["Persistence:ConnectionString"]))
        {
            services.AddAgentPersistence(configuration);
        }
        else
        {
            logger?.LogWarning("Persistence:ConnectionString is not set; tasks, cursors and audit are kept in memory only");
        }

        services.AddAgentCoreHostedServices();

        // Identity
        services.AddKeycloakAuthorization(configuration);
        if (configuration.GetValue("Ldap:Enabled", false))
        {
            services.AddLdapAuthorization(configuration);
        }

        // Capabilities
        services.AddAgentMcp(configuration);
        services.AddAgentCoding(configuration);
        services.AddAgentWorker(configuration);

        // Channels (each only when its base URL is configured)
        if (Configured(configuration, "Mattermost"))
        {
            services.AddMattermostChannel(configuration);
        }

        if (Configured(configuration, "Jira"))
        {
            services.AddJiraChannel(configuration);
        }

        if (Configured(configuration, "GitLab"))
        {
            services.AddGitLabChannel(configuration);
        }

        return services;
    }

    private static bool Configured(IConfiguration configuration, string section)
        => !string.IsNullOrWhiteSpace(configuration[$"{section}:BaseUrl"]) && configuration.GetValue($"{section}:Enabled", true);
}
