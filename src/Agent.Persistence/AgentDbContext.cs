using Agent.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agent.Persistence;

public sealed class AgentDbContext : DbContext
{
    public AgentDbContext(DbContextOptions<AgentDbContext> options)
        : base(options)
    {
    }

    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();

    public DbSet<TaskEventEntity> TaskEvents => Set<TaskEventEntity>();

    public DbSet<AuditEntity> Audit => Set<AuditEntity>();

    public DbSet<ProcessedEventEntity> ProcessedEvents => Set<ProcessedEventEntity>();

    public DbSet<ChannelCursorEntity> ChannelCursors => Set<ChannelCursorEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskEntity>(b =>
        {
            b.ToTable("Tasks");
            b.HasKey(t => t.Id);
            b.Property(t => t.Id).ValueGeneratedOnAdd();
            b.Property(t => t.Source).HasConversion<string>().HasMaxLength(32);
            b.Property(t => t.SourceRef).HasMaxLength(256).IsRequired();
            b.Property(t => t.SourceUrl).HasMaxLength(1024);
            b.Property(t => t.Title).HasMaxLength(512);
            b.Property(t => t.RequesterId).HasMaxLength(256).IsRequired();
            b.Property(t => t.RequesterName).HasMaxLength(256).IsRequired();
            b.Property(t => t.RequesterUsername).HasMaxLength(256);
            b.Property(t => t.NotifyChannel).HasConversion<string>().HasMaxLength(32);
            b.Property(t => t.ConversationId).HasMaxLength(512).IsRequired();
            b.Property(t => t.RepoUrl).HasMaxLength(1024);
            b.Property(t => t.ProjectId).HasMaxLength(256);
            b.Property(t => t.BaseBranch).HasMaxLength(256);
            b.Property(t => t.WorkBranch).HasMaxLength(256);
            b.Property(t => t.MergeRequestUrl).HasMaxLength(1024);
            b.Property(t => t.MergeRequestIid).HasMaxLength(64);
            b.Property(t => t.Status).HasConversion<string>().HasMaxLength(32);
            b.Property(t => t.Instruction).IsRequired();
            b.Property(t => t.WorkerId).HasMaxLength(128);
            b.Property(t => t.MetadataJson).IsRequired();
            b.Property(t => t.Version).IsConcurrencyToken();
            b.HasIndex(t => t.Status);
            b.HasIndex(t => new { t.Source, t.SourceRef });
            b.HasIndex(t => new { t.ProjectId, t.MergeRequestIid });
            b.HasIndex(t => t.RequesterId);
        });

        modelBuilder.Entity<TaskEventEntity>(b =>
        {
            b.ToTable("TaskEvents");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedOnAdd();
            b.Property(e => e.Type).HasMaxLength(64).IsRequired();
            b.Property(e => e.Message).IsRequired();
            b.HasIndex(e => e.TaskId);
            b.HasIndex(e => e.At);
        });

        modelBuilder.Entity<AuditEntity>(b =>
        {
            b.ToTable("Audit");
            b.HasKey(a => a.Id);
            b.Property(a => a.Id).ValueGeneratedOnAdd();
            b.Property(a => a.Channel).HasMaxLength(32).IsRequired();
            b.Property(a => a.CallerId).HasMaxLength(256).IsRequired();
            b.Property(a => a.CallerName).HasMaxLength(256);
            b.Property(a => a.Action).HasMaxLength(256).IsRequired();
            b.Property(a => a.Outcome).HasMaxLength(64).IsRequired();
            b.Property(a => a.Roles).HasMaxLength(64);
            b.HasIndex(a => a.Timestamp);
        });

        modelBuilder.Entity<ProcessedEventEntity>(b =>
        {
            b.ToTable("ProcessedEvents");
            b.HasKey(p => new { p.Channel, p.EventId });
            b.Property(p => p.Channel).HasMaxLength(32);
            b.Property(p => p.EventId).HasMaxLength(256);
            b.HasIndex(p => p.ProcessedAt);
        });

        modelBuilder.Entity<ChannelCursorEntity>(b =>
        {
            b.ToTable("ChannelCursors");
            b.HasKey(c => c.Key);
            b.Property(c => c.Key).HasMaxLength(256);
            b.Property(c => c.Value).HasMaxLength(1024).IsRequired();
        });
    }
}
