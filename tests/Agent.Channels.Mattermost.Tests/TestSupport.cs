using System.Text.Json;
using System.Threading.Channels;
using Agent.Core.Authorization;
using Agent.Core.Events;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Mattermost.Tests;

internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; set; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal sealed class FakeTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

/// <summary>An in-memory socket that yields scripted frames and records what the listener sends.</summary>
internal sealed class FakeSocket : IMattermostSocket
{
    private readonly Channel<string> _frames = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource _firstSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _sent = [];

    public Uri? ConnectedTo { get; private set; }

    public bool Disposed { get; private set; }

    public Task FirstSend => _firstSend.Task;

    public IReadOnlyList<string> Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToArray();
            }
        }
    }

    public void Enqueue(string frame) => _frames.Writer.TryWrite(frame);

    /// <summary>Simulates the server closing the connection after the queued frames were delivered.</summary>
    public void Close() => _frames.Writer.TryComplete();

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ConnectedTo = uri;
        return Task.CompletedTask;
    }

    public Task SendAsync(string json, CancellationToken cancellationToken)
    {
        lock (_sent)
        {
            _sent.Add(json);
        }

        _firstSend.TrySetResult();
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _frames.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeSocketFactory : IMattermostSocketFactory
{
    private readonly Func<int, FakeSocket> _create;
    private readonly List<FakeSocket> _sockets = [];
    private readonly Dictionary<int, TaskCompletionSource<FakeSocket>> _waiters = [];
    private readonly object _gate = new();

    public FakeSocketFactory(Func<int, FakeSocket> create) => _create = create;

    public IReadOnlyList<FakeSocket> Sockets
    {
        get
        {
            lock (_gate)
            {
                return _sockets.ToArray();
            }
        }
    }

    public IMattermostSocket Create()
    {
        FakeSocket socket;
        TaskCompletionSource<FakeSocket>? waiter;
        lock (_gate)
        {
            socket = _create(_sockets.Count + 1);
            _sockets.Add(socket);
            _waiters.Remove(_sockets.Count, out waiter);
        }

        waiter?.TrySetResult(socket);
        return socket;
    }

    /// <summary>Completes once the <paramref name="index"/>-th (1-based) socket has been created.</summary>
    public Task<FakeSocket> WaitForSocketAsync(int index, CancellationToken cancellationToken)
    {
        TaskCompletionSource<FakeSocket> waiter;
        lock (_gate)
        {
            if (_sockets.Count >= index)
            {
                return Task.FromResult(_sockets[index - 1]);
            }

            if (!_waiters.TryGetValue(index, out waiter!))
            {
                waiter = new TaskCompletionSource<FakeSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[index] = waiter;
            }
        }

        return waiter.Task.WaitAsync(cancellationToken);
    }
}

internal static class Fixtures
{
    public static readonly MattermostBotIdentity Bot = new("bot1", "agent");

    public static readonly MattermostUser BotUser = new() { Id = "bot1", Username = "agent", IsBot = true };

    public static readonly MattermostUser Alice = new() { Id = "u1", Username = "alice", Email = "alice@corp.local" };

    public static readonly MattermostUser Bob = new() { Id = "u2", Username = "bob", Email = "bob@corp.local" };

    public static readonly MattermostUser OtherBot = new() { Id = "b2", Username = "jenkins", IsBot = true };

    public static MattermostOptions Options(Action<MattermostOptions>? configure = null)
    {
        var options = new MattermostOptions
        {
            BaseUrl = "https://chat.example.test",
            BotToken = "secret-token",
            ReconnectDelaySeconds = 0,
            MaxReconnectDelaySeconds = 0,
        };
        configure?.Invoke(options);
        return options;
    }

    public static StaticOptionsMonitor<MattermostOptions> Monitor(Action<MattermostOptions>? configure = null) => new(Options(configure));

    public static MattermostPost Post(
        string id = "p1",
        string userId = "u1",
        string channelId = "c1",
        string message = "hello",
        string rootId = "",
        long createAt = 1000,
        string type = "")
        => new()
        {
            Id = id,
            UserId = userId,
            ChannelId = channelId,
            Message = message,
            RootId = rootId,
            CreateAt = createAt,
            UpdateAt = createAt,
            Type = type,
        };

    public static MattermostPostedEvent Posted(MattermostPost post, string channelType, params string[] mentions)
        => new(post, channelType, mentions, ChannelName: "town-square", SenderName: "@alice");

    public static MattermostUserLookup Users(params MattermostUser[] users)
        => (id, _) => Task.FromResult(users.FirstOrDefault(u => u.Id == id));

    public static MattermostThreadLookup Thread(params MattermostPost[] posts)
        => (_, _) => Task.FromResult<IReadOnlyList<MattermostPost>>(posts.OrderBy(p => p.CreateAt).ToList());

    public static MattermostThreadLookup NoThread()
        => (rootId, _) => throw new InvalidOperationException($"Thread lookup for {rootId} was not expected.");

    public static InboundEvent Event(
        string postId = "p1",
        string channelId = "c1",
        string rootId = "",
        string channelType = "O",
        string text = "hello",
        long createAt = 1000)
        => new()
        {
            Channel = Agent.Core.Channels.Channel.Mattermost,
            EventId = postId,
            Caller = new CallerIdentity(Agent.Core.Channels.Channel.Mattermost, "u1", "alice"),
            ConversationId = string.IsNullOrEmpty(rootId) ? (channelType == "D" ? channelId : postId) : rootId,
            Text = text,
            IsPrivate = channelType == "D",
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(createAt),
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MattermostEventMapper.PostIdKey] = postId,
                [MattermostEventMapper.ChannelIdKey] = channelId,
                [MattermostEventMapper.RootIdKey] = rootId,
                [MattermostEventMapper.ChannelTypeKey] = channelType,
                [MattermostEventMapper.UserIdKey] = "u1",
            },
        };

    public const string AuthOkFrame = """{"status":"OK","seq_reply":1}""";

    public const string AuthFailFrame = """{"status":"FAIL","seq_reply":1,"error":{"id":"api.web_socket_handler.authentication_challenge.app_error","message":"Invalid or expired session"}}""";

    public const string HelloFrame = """{"event":"hello","data":{"connection_id":"abc","server_version":"9.5.0"},"broadcast":{},"seq":1}""";

    public static string PostedFrame(MattermostPost post, string channelType, string[]? mentions = null, int seq = 2, string channelName = "town-square")
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["channel_display_name"] = channelName,
            ["channel_name"] = channelName,
            ["channel_type"] = channelType,
            ["post"] = JsonSerializer.Serialize(post, MattermostJson.Options),
            ["sender_name"] = "@alice",
            ["set_online"] = true,
            ["team_id"] = "t1",
        };
        if (mentions is not null)
        {
            data["mentions"] = JsonSerializer.Serialize(mentions);
        }

        return JsonSerializer.Serialize(new
        {
            @event = "posted",
            data,
            broadcast = new { omit_users = (object?)null, user_id = "", channel_id = post.ChannelId, team_id = "" },
            seq,
        });
    }

    public static CancellationToken Timeout(TimeSpan? after = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(after ?? TimeSpan.FromSeconds(10));
        return cts.Token;
    }

    public static async Task<InboundEvent> ReadOneAsync(IInboundQueue queue, CancellationToken cancellationToken)
    {
        await foreach (var evt in queue.ReadAllAsync(cancellationToken))
        {
            return evt;
        }

        throw new InvalidOperationException("The queue completed without an event.");
    }

    public static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
        }
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        while (!await condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
        }
    }
}
