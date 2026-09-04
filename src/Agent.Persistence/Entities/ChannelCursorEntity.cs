namespace Agent.Persistence.Entities;

public sealed class ChannelCursorEntity
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
