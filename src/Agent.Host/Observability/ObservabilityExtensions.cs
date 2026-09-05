using Agent.Core.Observability;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Agent.Host.Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// Wires OpenTelemetry for traces, metrics and logs. The agent's own instrumentation lives in
    /// <see cref="AgentTelemetry"/> and does not depend on this: with telemetry disabled, or no exporter
    /// configured, the same code runs and simply ships nothing.
    /// </summary>
    public static IServiceCollection AddAgentObservability(this IServiceCollection services, IConfiguration configuration, ILoggingBuilder? logging = null)
    {
        services.AddOptions<ObservabilityOptions>().Bind(configuration.GetSection(ObservabilityOptions.SectionName));

        var options = configuration.GetSection(ObservabilityOptions.SectionName).Get<ObservabilityOptions>() ?? new ObservabilityOptions();
        if (!options.Enabled)
        {
            return services;
        }

        var environment = options.Environment
            ?? System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        var builder = services.AddOpenTelemetry().ConfigureResource(resource =>
        {
            resource.AddService(options.ServiceName, serviceVersion: AgentTelemetry.Version, serviceInstanceId: System.Environment.MachineName);
            if (!string.IsNullOrWhiteSpace(environment))
            {
                resource.AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", environment)]);
            }
        });

        if (options.Traces)
        {
            builder.WithTracing(tracing =>
            {
                tracing
                    .AddSource(AgentTelemetry.ActivitySourceName)
                    .AddSource(AgentTelemetry.LlmActivitySourceName)
                    .AddSource("Experimental.Microsoft.Extensions.AI")
                    .SetSampler(options.SampleRatio >= 1
                        ? new AlwaysOnSampler()
                        : new TraceIdRatioBasedSampler(Math.Max(0, options.SampleRatio)));

                tracing.AddAspNetCoreInstrumentation(o => o.Filter = context =>
                    options.TraceHealthChecks || !context.Request.Path.StartsWithSegments("/healthz") && !context.Request.Path.StartsWithSegments("/readyz"));

                if (options.HttpClientInstrumentation)
                {
                    tracing.AddHttpClientInstrumentation();
                }

                if (options.SqlInstrumentation)
                {
                    tracing.AddSqlClientInstrumentation();
                }

                ApplyExporters(tracing, options);
            });
        }

        if (options.Metrics)
        {
            builder.WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(AgentTelemetry.MeterName)
                    .AddMeter("Microsoft.Extensions.AI")
                    .AddAspNetCoreInstrumentation();

                if (options.HttpClientInstrumentation)
                {
                    metrics.AddHttpClientInstrumentation();
                }

                if (options.RuntimeInstrumentation)
                {
                    metrics.AddRuntimeInstrumentation();
                }

                ApplyExporters(metrics, options);
            });
        }

        if (options.Logs && logging is not null)
        {
            logging.AddOpenTelemetry(log =>
            {
                log.IncludeScopes = true;
                log.IncludeFormattedMessage = true;
                log.ParseStateValues = true;

                if (HasEndpoint(options))
                {
                    log.AddOtlpExporter(exporter => Configure(exporter, options));
                }

                if (options.ConsoleExporter)
                {
                    log.AddConsoleExporter();
                }
            });
        }

        return services;
    }

    private static void ApplyExporters(TracerProviderBuilder tracing, ObservabilityOptions options)
    {
        if (HasEndpoint(options))
        {
            tracing.AddOtlpExporter(exporter => Configure(exporter, options));
        }

        if (options.ConsoleExporter)
        {
            tracing.AddConsoleExporter();
        }
    }

    private static void ApplyExporters(MeterProviderBuilder metrics, ObservabilityOptions options)
    {
        if (HasEndpoint(options))
        {
            metrics.AddOtlpExporter(exporter => Configure(exporter, options));
        }

        if (options.ConsoleExporter)
        {
            metrics.AddConsoleExporter();
        }
    }

    /// <summary>True when an endpoint is configured here or through the standard OTEL environment variable.</summary>
    internal static bool HasEndpoint(ObservabilityOptions options)
        => !string.IsNullOrWhiteSpace(options.OtlpEndpoint)
           || !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

    private static void Configure(OtlpExporterOptions exporter, ObservabilityOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
        {
            exporter.Endpoint = new Uri(options.OtlpEndpoint);
        }

        exporter.Protocol = options.OtlpProtocol.Replace("/", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "httpprotobuf" or "http" => OtlpExportProtocol.HttpProtobuf,
            _ => OtlpExportProtocol.Grpc,
        };

        if (options.OtlpHeaders.Count > 0)
        {
            exporter.Headers = string.Join(",", options.OtlpHeaders.Select(h => $"{h.Key}={h.Value}"));
        }
    }
}
