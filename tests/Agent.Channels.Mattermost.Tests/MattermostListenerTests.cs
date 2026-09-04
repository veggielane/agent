using System.Text.Json;
using Agent.Core.Events;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Agent.Channels.Mattermost.Tests.Fixtures;

namespace Agent.Channels.Mattermost.Tests;

public sealed class MattermostListenerTests
{
    private sealed record Harness(MattermostListener Listener, InboundQueue Queue, InMemoryCursorStore Cursors, IMattermostClient Client);

    private static Harness Build(IMattermostSocketFactory sockets, Action<MattermostOptions>? configure = null)
    {
        var client = Substitute.For<IMattermostClient>();
        client.GetMeAsync(Arg.Any<CancellationToken>()).Returns(BotUser);
        client.GetUserAsync("u1", Arg.Any<CancellationToken>()).Returns(Alice);
        client.GetUserAsync("u2", Arg.Any<CancellationToken>()).Returns(Bob);

        var options = Monitor(configure);
        var users = new MattermostUserDirectory(client, new MemoryCache(new MemoryCacheOptions()));
        var mapper = new MattermostEventMapper(options);
        var queue = new InboundQueue();
        var cursors = new InMemoryCursorStore();
        var listener = new MattermostListener(sockets, client, users, mapper, queue, cursors, options, NullLogger<MattermostListener>.Instance);
        return new Harness(listener, queue, cursors, client);
    }

    [Fact]
    public async Task ExecuteAsync_ConnectsToDerivedSocketUri_AndSendsAuthChallengeFirst()
    {
        var ct = Timeout();
        var socket = new FakeSocket();
        var factory = new FakeSocketFactory(_ => socket);
        var harness = Build(factory);

        await harness.Listener.StartAsync(ct);
        try
        {
            await socket.FirstSend.WaitAsync(ct);

            Assert.Equal(new Uri("wss://chat.example.test/api/v4/websocket"), socket.ConnectedTo);
            var first = JsonDocument.Parse(socket.Sent[0]).RootElement;
            Assert.Equal(1, first.GetProperty("seq").GetInt32());
            Assert.Equal("authentication_challenge", first.GetProperty("action").GetString());
            Assert.Equal("secret-token", first.GetProperty("data").GetProperty("token").GetString());
            Assert.StartsWith("connected since", harness.Listener.Status, StringComparison.Ordinal);
            Assert.Equal("Mattermost", harness.Listener.Name);
        }
        finally
        {
            await harness.Listener.StopAsync(CancellationToken.None);
        }

        Assert.True(socket.Disposed);
        Assert.StartsWith("stopped", harness.Listener.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_PostedFrame_EnqueuesOneEventAndAdvancesCursor()
    {
        var ct = Timeout();
        var socket = new FakeSocket();
        socket.Enqueue(AuthOkFrame);
        socket.Enqueue(HelloFrame);
        socket.Enqueue(PostedFrame(Post(message: "hello bot", createAt: 1725400000000), "D"));
        var factory = new FakeSocketFactory(_ => socket);
        var harness = Build(factory);

        await harness.Listener.StartAsync(ct);
        try
        {
            var evt = await ReadOneAsync(harness.Queue, ct);

            Assert.Equal("p1", evt.EventId);
            Assert.Equal("hello bot", evt.Text);
            Assert.True(evt.IsPrivate);
            Assert.Equal("c1", evt.ConversationId);
            Assert.Equal("alice", evt.Caller.Username);
            await WaitUntilAsync(async () => await harness.Cursors.GetAsync(MattermostListener.CursorKey, ct) == "1725400000000", ct);
            Assert.Equal(0, harness.Queue.Depth);
        }
        finally
        {
            await harness.Listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ChannelMention_EnqueuesThreadedEvent()
    {
        var ct = Timeout();
        var socket = new FakeSocket();
        socket.Enqueue(AuthOkFrame);
        socket.Enqueue(PostedFrame(Post(message: "@agent summarise this", rootId: "r1"), "O", ["bot1"]));
        var harness = Build(new FakeSocketFactory(_ => socket));

        await harness.Listener.StartAsync(ct);
        try
        {
            var evt = await ReadOneAsync(harness.Queue, ct);

            Assert.Equal("summarise this", evt.Text);
            Assert.Equal("r1", evt.ConversationId);
            Assert.False(evt.IsPrivate);
        }
        finally
        {
            await harness.Listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_IgnoredPosts_AreNotEnqueued()
    {
        var ct = Timeout();
        var socket = new FakeSocket();
        socket.Enqueue(AuthOkFrame);
        socket.Enqueue(PostedFrame(Post(id: "own", userId: "bot1", message: "my own reply"), "D"));
        socket.Enqueue(PostedFrame(Post(id: "sys", message: "joined", type: "system_join_channel"), "D"));
        socket.Enqueue(PostedFrame(Post(id: "chan", message: "no mention here"), "O"));
        socket.Enqueue("this is not json");
        socket.Enqueue(PostedFrame(Post(id: "real", message: "hello"), "D"));
        var harness = Build(new FakeSocketFactory(_ => socket));

        await harness.Listener.StartAsync(ct);
        try
        {
            var evt = await ReadOneAsync(harness.Queue, ct);

            Assert.Equal("real", evt.EventId);
        }
        finally
        {
            await harness.Listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MissingChannelType_IsResolvedThroughTheApi()
    {
        var ct = Timeout();
        var socket = new FakeSocket();
        socket.Enqueue(AuthOkFrame);
        var frame = PostedFrame(Post(message: "hello"), "D").Replace("\"channel_type\":\"D\"", "\"channel_type\":\"\"", StringComparison.Ordinal);
        socket.Enqueue(frame);
        var harness = Build(new FakeSocketFactory(_ => socket));
        harness.Client.GetChannelAsync("c1", Arg.Any<CancellationToken>()).Returns(new MattermostChannel { Id = "c1", Type = "D", Name = "bot1__u1" });

        await harness.Listener.StartAsync(ct);
        try
        {
            var evt = await ReadOneAsync(harness.Queue, ct);

            Assert.True(evt.IsPrivate);
            Assert.Equal("D", evt.Meta("channel_type"));
        }
        finally
        {
            await harness.Listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_SocketClosed_ReconnectsAndAuthenticatesAgain()
    {
        var ct = Timeout();
        var factory = new FakeSocketFactory(index =>
        {
            var socket = new FakeSocket();
            socket.Enqueue(AuthOkFrame);
            if (index == 1)
            {
                socket.Close();
            }

            return socket;
        });
        var harness = Build(factory);

        await harness.Listener.StartAsync(ct);
        try
        {
            var second = await factory.WaitForSocketAsync(2, ct);
            await second.FirstSend.WaitAsync(ct);

            Assert.Equal(2, factory.Sockets.Count);
            Assert.True(factory.Sockets[0].Disposed);
            Assert.All(factory.Sockets, s => Assert.Contains("authentication_challenge", s.Sent[0], StringComparison.Ordinal));
            Assert.Equal(2, harness.Listener.Connections);
        }
        finally
        {
            await harness.Listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AuthRejected_ReportsErrorAndRetries()
    {
        var ct = Timeout();
        var factory = new FakeSocketFactory(index =>
        {
            var socket = new FakeSocket();
            socket.Enqueue(index == 1 ? AuthFailFrame : AuthOkFrame);
            return socket;
        });
        var harness = Build(factory);

        await harness.Listener.StartAsync(ct);
        try
        {
            var second = await factory.WaitForSocketAsync(2, ct);
            await second.FirstSend.WaitAsync(ct);

            Assert.True(factory.Sockets[0].Disposed);
        }
        finally
        {
            await harness.Listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AfterReconnect_RefetchesDmChannelPostsSinceCursor()
    {
        var ct = Timeout();
        var factory = new FakeSocketFactory(index =>
        {
            var socket = new FakeSocket();
            socket.Enqueue(AuthOkFrame);
            if (index == 1)
            {
                socket.Enqueue(PostedFrame(Post(id: "p1", message: "before the drop", createAt: 1000), "D"));
                socket.Close();
            }

            return socket;
        });
        var harness = Build(factory);
        harness.Client.GetPostsForChannelAsync("c1", 1000, null, Arg.Any<CancellationToken>())
            .Returns([
                Post(id: "p1", message: "before the drop", createAt: 1000),
                Post(id: "p2", message: "while disconnected", createAt: 2000),
                Post(id: "own", userId: "bot1", message: "bot reply", createAt: 1500),
            ]);

        await harness.Listener.StartAsync(ct);
        try
        {
            var first = await ReadOneAsync(harness.Queue, ct);
            var second = await ReadOneAsync(harness.Queue, ct);

            Assert.Equal("p1", first.EventId);
            Assert.Equal("p2", second.EventId);
            Assert.Equal("while disconnected", second.Text);
            await WaitUntilAsync(async () => await harness.Cursors.GetAsync(MattermostListener.CursorKey, ct) == "2000", ct);
        }
        finally
        {
            await harness.Listener.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("https://chat.example.test", "wss://chat.example.test/api/v4/websocket")]
    [InlineData("https://chat.example.test/", "wss://chat.example.test/api/v4/websocket")]
    [InlineData("http://localhost:8065/mattermost", "ws://localhost:8065/mattermost/api/v4/websocket")]
    public void BuildSocketUri_DerivesWebSocketAddress(string baseUrl, string expected)
    {
        Assert.Equal(new Uri(expected), MattermostListener.BuildSocketUri(baseUrl));
    }
}
