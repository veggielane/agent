using Agent.Infrastructure.Tests.Support;
using Agent.Persistence.Entities;
using Agent.Persistence.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Agent.Infrastructure.Tests.Persistence;

public sealed class RetentionTests : IDisposable
{
    private readonly SqliteDatabase _db = new();

    [Fact]
    public async Task PurgeAsync_DeletesOnlyRowsOlderThanCutoff_InBatches()
    {
        var cutoff = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var old = cutoff.AddDays(-1).UtcDateTime;
        var recent = cutoff.AddDays(1).UtcDateTime;

        await using (var ctx = await _db.Factory.CreateDbContextAsync())
        {
            for (var i = 0; i < 5; i++)
            {
                ctx.TaskEvents.Add(new TaskEventEntity { TaskId = 1, At = old, Type = "status", Message = $"old {i}" });
                ctx.Audit.Add(new AuditEntity { Timestamp = old, Channel = "Cli", CallerId = "c", Action = "a", Outcome = "allowed" });
                ctx.ProcessedEvents.Add(new ProcessedEventEntity { Channel = "Jira", EventId = $"old-{i}", ProcessedAt = old });
            }

            ctx.TaskEvents.Add(new TaskEventEntity { TaskId = 1, At = recent, Type = "status", Message = "recent" });
            ctx.TaskEvents.Add(new TaskEventEntity { TaskId = 1, At = cutoff.UtcDateTime, Type = "status", Message = "at cutoff" });
            ctx.Audit.Add(new AuditEntity { Timestamp = recent, Channel = "Cli", CallerId = "c", Action = "a", Outcome = "allowed" });
            ctx.ProcessedEvents.Add(new ProcessedEventEntity { Channel = "Jira", EventId = "recent", ProcessedAt = recent });
            ctx.Tasks.Add(new TaskEntity { SourceRef = "PROJ-1", RequesterId = "r", RequesterName = "r", ConversationId = "c", Instruction = "i", CreatedAt = old, UpdatedAt = old, Version = 1 });
            await ctx.SaveChangesAsync();
        }

        var purger = new RetentionPurger(_db.Factory, new ListLogger<RetentionPurger>());

        var result = await purger.PurgeAsync(cutoff, batchSize: 2);

        Assert.Equal(new RetentionResult(5, 5, 5), result);
        Assert.Equal(15, result.Total);

        await using var verify = await _db.Factory.CreateDbContextAsync();
        Assert.Equal(["at cutoff", "recent"], (await verify.TaskEvents.OrderBy(e => e.At).Select(e => e.Message).ToListAsync()));
        Assert.Equal(1, await verify.Audit.CountAsync());
        Assert.Equal("recent", (await verify.ProcessedEvents.SingleAsync()).EventId);
        Assert.Equal(1, await verify.Tasks.CountAsync());

        var again = await purger.PurgeAsync(cutoff, batchSize: 2);
        Assert.Equal(0, again.Total);
    }

    public void Dispose() => _db.Dispose();
}
