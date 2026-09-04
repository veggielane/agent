using Agent.Coding;
using Agent.Core.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent.Worker;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the task runner and the hosted <see cref="TaskWorker"/> (also exposed as an
    /// <see cref="IStatusContributor"/>). Adds the coding engine when it has not been registered yet.
    /// </summary>
    public static IServiceCollection AddAgentWorker(this IServiceCollection services, IConfiguration configuration)
    {
        if (!services.Any(d => d.ServiceType == typeof(ICodingEngine)))
        {
            services.AddAgentCoding(configuration);
        }

        services.TryAddSingleton<ITaskRunner, TaskRunner>();
        services.TryAddSingleton<TaskWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<TaskWorker>());
        services.AddSingleton<IStatusContributor>(sp => sp.GetRequiredService<TaskWorker>());
        return services;
    }
}
