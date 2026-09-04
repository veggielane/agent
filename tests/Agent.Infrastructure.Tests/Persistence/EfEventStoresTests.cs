using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Persistence.Stores;
using Microsoft.EntityFrameworkCore;

namespace Agent.Infrastructure.Tests.Persistence;

public sealed class EfProcessedEventStoreTests : IDisposable
{
    private readonly SqliteDatabase _db = new();

    [Fact]
    public async Task TryMarkProcessedAsync_FirstTimeTrue_DuplicateFalse()
    {
        var store = _db.Get<IProcessedEventStore>();
        Assert.IsType<EfProcessedEventStore>(store);

        Assert.True(await store.TryMarkProcessedAsync(Channel.Jira, "PROJ-1:comment:10"));
        Assert.False(await store.TryMarkProcessedAsync(Channel.Jira, "PROJ-1:comment:10"));
        Assert.True(await store.TryMarkProcessedAsync(Channel.GitLab, "PROJ-1:comment:10"));

        await using var ctx = await _db.Factory.CreateDbContextAsync();
        var rows = await ctx.ProcessedEvents.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(_db.Time.Now.UtcDateTime, r.ProcessedAt));
    }

    [Fact]
    public async Task TryMarkProcessedAsync_RowInsertedBehindItsBack_ReturnsFalse()
    {
        var store = _db.Get<IProcessedEventStore>();

        await using (var ctx = await _db.Factory.CreateDbContextAsync())
        {
            ctx.ProcessedEvents.Add(new Agent.Persistence.Entities.ProcessedEventEntity { Channel = "Mattermost", EventId = "post-1", ProcessedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        Assert.False(await store.TryMarkProcessedAsync(Channel.Mattermost, "post-1"));
    }

    public void Dispose() => _db.Dispose();
}

public sealed class EfCursorStoreTests : IDisposable
{
    private readonly SqliteDatabase _db = new();

    [Fact]
    public async Task SetAsync_InsertsThenOverwrites()
    {
        var store = _db.Get<ICursorStore>();
        Assert.IsType<EfCursorStore>(store);

        Assert.Null(await store.GetAsync("jira:PROJ"));

        await store.SetAsync("jira:PROJ", "2026-09-04T10:00:00Z");
        Assert.Equal("2026-09-04T10:00:00Z", await store.GetAsync("jira:PROJ"));

        _db.Time.Advance(TimeSpan.FromMinutes(5));
        await store.SetAsync("jira:PROJ", "2026-09-04T10:05:00Z");
        await store.SetAsync("gitlab:team", "1234");

        Assert.Equal("2026-09-04T10:05:00Z", await store.GetAsync("jira:PROJ"));
        Assert.Equal("1234", await store.GetAsync("gitlab:team"));

        await using var ctx = await _db.Factory.CreateDbContextAsync();
        var rows = await ctx.ChannelCursors.OrderBy(c => c.Key).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(_db.Time.Now.UtcDateTime, rows.Single(r => r.Key == "jira:PROJ").UpdatedAt);
    }

    public void Dispose() => _db.Dispose();
}
