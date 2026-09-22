using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Core.Tests.Tasks;

public sealed class TaskNotifierRouterTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly InMemoryProcessedEventStore _processed = new();
    private readonly RecordingNotifier _gitlab = new(Channel.GitLab);
    private readonly RecordingMirror _ops = new(Channel.Mattermost, enabled: true);

    private static AgentTask Task_(int id = 7, Channel channel = Channel.GitLab) => new()
    {
        Id = id,
        Source = TaskSource.GitLabIssue,
        SourceRef = "team/repo#12",
        NotifyChannel = channel,
        ConversationId = "team/repo#12",
    };

    private TaskNotifierRouter Router(params ITaskNotificationMirror[] mirrors)
        => new([_gitlab], _processed, mirrors, NullLogger<TaskNotifierRouter>.Instance);

    [Fact]
    public async Task NotifyAsync_Keyed_PostsOncePerKeyAndTask()
    {
        var router = Router();

        await router.NotifyAsync(Task_(), new TaskNotification("published:1", "Opened MR"), Ct);
        await router.NotifyAsync(Task_(), new TaskNotification("published:1", "Opened MR again"), Ct);
        await router.NotifyAsync(Task_(), new TaskNotification("published:2", "Opened MR after retry"), Ct);
        await router.NotifyAsync(Task_(id: 8), new TaskNotification("published:1", "Other task"), Ct);

        Assert.Equal(["Opened MR", "Opened MR after retry", "Other task"], _gitlab.Posted);
    }

    [Fact]
    public async Task NotifyAsync_Unkeyed_PostsEveryTime()
    {
        var router = Router();

        await router.NotifyAsync(Task_(), "progress", Ct);
        await router.NotifyAsync(Task_(), "progress", Ct);

        Assert.Equal(["progress", "progress"], _gitlab.Posted);
    }

    [Fact]
    public async Task NotifyAsync_Terminal_IsMirroredOnce_NonTerminalStaysOnTheThread()
    {
        var router = Router(_ops);

        await router.NotifyAsync(Task_(), new TaskNotification("plan", "The plan"), Ct);
        await router.NotifyAsync(Task_(), new TaskNotification("failed:1", "Task failed", Terminal: true), Ct);
        await router.NotifyAsync(Task_(), new TaskNotification("failed:1", "Task failed", Terminal: true), Ct);

        Assert.Equal(["The plan", "Task failed"], _gitlab.Posted);
        var mirrored = Assert.Single(_ops.Mirrored);
        Assert.Equal("failed:1", mirrored.Notification.Key);
        Assert.Equal(7, mirrored.Task.Id);
    }

    [Fact]
    public async Task NotifyAsync_WithoutANotifierForTheChannel_StillMirrorsTerminalEvents()
    {
        var router = Router(_ops);

        await router.NotifyAsync(Task_(channel: Channel.Cli), new TaskNotification("published:1", "Opened MR", Terminal: true), Ct);

        Assert.Empty(_gitlab.Posted);
        Assert.Single(_ops.Mirrored);
    }

    [Fact]
    public async Task MirrorAsync_DisabledMirror_IsSkippedWithoutConsumingTheKey()
    {
        var mirror = new RecordingMirror(Channel.Mattermost, enabled: false);
        var router = Router(mirror);
        var notification = new TaskNotification("pipeline-gave-up:900", "CI still red", Terminal: true);

        await router.MirrorAsync(Task_(), notification, Ct);
        Assert.Empty(mirror.Mirrored);

        mirror.Enabled = true;
        await router.MirrorAsync(Task_(), notification, Ct);
        Assert.Single(mirror.Mirrored);
    }

    [Fact]
    public async Task MirrorAsync_OneMirrorFailing_DoesNotStopTheOthers()
    {
        var broken = new RecordingMirror(Channel.Jira, enabled: true) { Fail = true };
        var router = Router(broken, _ops);

        await router.MirrorAsync(Task_(), new TaskNotification("failed:1", "Task failed", Terminal: true), Ct);

        Assert.Single(_ops.Mirrored);
    }

    [Fact]
    public async Task NotifyAsync_NotifierAndMirrorKeys_DoNotCollideOnTheSameChannel()
    {
        var chat = new RecordingNotifier(Channel.Mattermost);
        var router = new TaskNotifierRouter([chat], _processed, [_ops], NullLogger<TaskNotifierRouter>.Instance);

        await router.NotifyAsync(Task_(channel: Channel.Mattermost), new TaskNotification("published:1", "Opened MR", Terminal: true), Ct);

        Assert.Single(chat.Posted);
        Assert.Single(_ops.Mirrored);
    }

    private sealed class RecordingNotifier(Channel channel) : ITaskNotifier
    {
        public Channel Channel { get; } = channel;

        public List<string> Posted { get; } = [];

        public Task NotifyAsync(AgentTask task, string markdown, CancellationToken cancellationToken)
        {
            Posted.Add(markdown);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMirror(Channel channel, bool enabled) : ITaskNotificationMirror
    {
        public Channel Channel { get; } = channel;

        public bool Enabled { get; set; } = enabled;

        public bool Fail { get; init; }

        public List<(AgentTask Task, TaskNotification Notification)> Mirrored { get; } = [];

        public Task MirrorAsync(AgentTask task, TaskNotification notification, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("mirror down");
            }

            Mirrored.Add((task, notification));
            return Task.CompletedTask;
        }
    }
}
