namespace Agent.Persistence.Entities;

public sealed class AuditEntity
{
    public long Id { get; set; }

    public DateTime Timestamp { get; set; }

    public string Channel { get; set; } = string.Empty;

    public string CallerId { get; set; } = string.Empty;

    public string? CallerName { get; set; }

    public string Action { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string? Detail { get; set; }

    /// <summary>Comma-separated role names; null when roles were not resolved.</summary>
    public string? Roles { get; set; }
}
