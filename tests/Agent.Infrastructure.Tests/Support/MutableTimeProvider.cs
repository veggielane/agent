namespace Agent.Infrastructure.Tests.Support;

/// <summary>A clock the test moves by hand.</summary>
public sealed class MutableTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
