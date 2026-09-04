namespace Agent.Persistence.Entities;

public sealed class TaskEventEntity
{
    public long Id { get; set; }

    public int TaskId { get; set; }

    public DateTime At { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}
