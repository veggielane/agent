namespace Agent.Host.Observability;

/// <summary>
/// Bound from the <c>Telemetry</c> section. Traces, metrics and logs are exported over OTLP when an
/// endpoint is configured; with no endpoint the agent still records everything internally but ships
/// nothing, so instrumentation is always safe to leave in.
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Telemetry";

    public bool Enabled { get; set; } = true;

    /// <summary>Reported as <c>service.name</c>. Defaults to the process name.</summary>
    public string ServiceName { get; set; } = "team-agent";

    /// <summary>Reported as <c>deployment.environment.name</c>, e.g. production or staging.</summary>
    public string? Environment { get; set; }

    /// <summary>
    /// OTLP collector endpoint, e.g. <c>http://otel-collector:4317</c>. Empty means nothing is exported;
    /// the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment variable is honoured too.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>grpc (default, port 4317) or httpprotobuf (port 4318).</summary>
    public string OtlpProtocol { get; set; } = "grpc";

    /// <summary>Extra OTLP headers, typically an API key for a hosted backend.</summary>
    public Dictionary<string, string> OtlpHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Traces { get; set; } = true;

    public bool Metrics { get; set; } = true;

    public bool Logs { get; set; } = true;

    /// <summary>Also writes telemetry to stdout. For development only: it is verbose.</summary>
    public bool ConsoleExporter { get; set; }

    /// <summary>Fraction of traces kept, 0 to 1. 1 keeps everything, which is usually right at this volume.</summary>
    public double SampleRatio { get; set; } = 1;

    /// <summary>Traces outbound HTTP to Mattermost, Jira, GitLab, Keycloak and the LLM endpoint.</summary>
    public bool HttpClientInstrumentation { get; set; } = true;

    /// <summary>Traces database calls. Only produces spans on SQL Server.</summary>
    public bool SqlInstrumentation { get; set; } = true;

    /// <summary>Adds process and GC metrics.</summary>
    public bool RuntimeInstrumentation { get; set; } = true;

    /// <summary>Health endpoints are noise on a dashboard; excluded by default.</summary>
    public bool TraceHealthChecks { get; set; }
}
