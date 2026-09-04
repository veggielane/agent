namespace Agent.Persistence.Entities;

public sealed class ProcessedEventEntity
{
    public string Channel { get; set; } = string.Empty;

    public string EventId { get; set; } = string.Empty;

    public DateTime ProcessedAt { get; set; }
}
