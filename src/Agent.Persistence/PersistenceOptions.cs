using System.ComponentModel.DataAnnotations;

namespace Agent.Persistence;

public enum PersistenceProvider
{
    SqlServer,
    Sqlite,
}

public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    /// <summary>"SqlServer" (default) or "Sqlite" (development / tests).</summary>
    public PersistenceProvider Provider { get; set; } = PersistenceProvider.SqlServer;

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Apply migrations (SQL Server) or create the schema (SQLite) when the host starts.</summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>Age after which task events, audit rows and processed-event markers are purged.</summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 90;

    /// <summary>How often the retention job runs.</summary>
    [Range(1, 24 * 30)]
    public int RetentionIntervalHours { get; set; } = 24;

    /// <summary>Delay before the first retention run after startup.</summary>
    [Range(0, 3600)]
    public int RetentionStartupDelaySeconds { get; set; } = 60;

    /// <summary>Rows deleted per statement by the retention job.</summary>
    [Range(100, 100_000)]
    public int RetentionBatchSize { get; set; } = 5000;
}
