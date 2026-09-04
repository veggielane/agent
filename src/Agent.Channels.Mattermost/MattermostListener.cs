using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Agent.Core.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Mattermost;

/// <summary>
/// Keeps a WebSocket to Mattermost open, turns <c>posted</c> events into <see cref="InboundEvent"/>s and
/// reconnects with exponential backoff when the socket drops.
/// </summary>
public sealed class MattermostListener : BackgroundService, IEventSource
{
    public const string CursorKey = "mattermost:last_create_at";
    public const string WebSocketPath = "api/v4/websocket";

    private const int AuthSeq = 1;
    private const int MaxTrackedDmChannels = 200;

    private readonly IMattermostSocketFactory _sockets;
    private readonly IMattermostClient _client;
    private readonly IMattermostUserDirectory _users;
    private readonly MattermostEventMapper _mapper;
    private readonly IInboundQueue _queue;
    private readonly ICursorStore _cursors;
    private readonly IOptionsMonitor<MattermostOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MattermostListener> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _dmChannels = new(StringComparer.Ordinal);

    private volatile string _status = "not started";
    private int _connections;

    public MattermostListener(
        IMattermostSocketFactory sockets,
        IMattermostClient client,
        IMattermostUserDirectory users,
        MattermostEventMapper mapper,
        IInboundQueue queue,
        ICursorStore cursors,
        IOptionsMonitor<MattermostOptions> options,
        ILogger<MattermostListener> logger,
        TimeProvider? timeProvider = null)
    {
        _sockets = sockets;
        _client = client;
        _users = users;
        _mapper = mapper;
        _queue = queue;
        _cursors = cursors;
        _options = options;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Name => "Mattermost";

    public string Status => _status;

    /// <summary>How many times a socket was opened since start; useful for diagnostics and tests.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Derives <c>wss://host/api/v4/websocket</c> from the REST base URL.</summary>
    public static Uri BuildSocketUri(string baseUrl)
    {
        var rest = MattermostClient.NormalizeBaseUrl(baseUrl);
        var builder = new UriBuilder(rest)
        {
            Scheme = string.Equals(rest.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = rest.AbsolutePath.TrimEnd('/') + "/" + WebSocketPath,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    /// <summary>The first frame sent after connecting.</summary>
    public static string BuildAuthChallenge(string token)
        => JsonSerializer.Serialize(new { seq = AuthSeq, action = "authentication_challenge", data = new { token } });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = InitialDelay();
        while (!stoppingToken.IsCancellationRequested)
        {
            var authenticated = false;
            try
            {
                authenticated = await RunSessionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mattermost connection failed: {Message}", ex.Message);
                SetStatus($"error at {Now():O}: {ex.Message}");
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (authenticated)
            {
                delay = InitialDelay();
            }

            _logger.LogInformation("Reconnecting to Mattermost in {Delay}", delay);
            try
            {
                await Task.Delay(delay, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = NextDelay(delay);
        }

        SetStatus($"stopped at {Now():O}");
    }

    /// <summary>One socket lifetime. Returns true when the server accepted the token.</summary>
    private async Task<bool> RunSessionAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var me = await _users.GetMeAsync(cancellationToken).ConfigureAwait(false);
        var uri = BuildSocketUri(options.BaseUrl);

        await using var socket = _sockets.Create();
        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        await socket.SendAsync(BuildAuthChallenge(options.BotToken), cancellationToken).ConfigureAwait(false);

        var attempt = Interlocked.Increment(ref _connections);
        var connectedAt = Now();
        SetStatus($"connected since {connectedAt:O} as @{me.Username} (connection #{attempt})");
        _logger.LogInformation("Connected to Mattermost at {Uri} as @{Username}", uri, me.Username);

        await RecoverGapAsync(me, attempt, cancellationToken).ConfigureAwait(false);

        var authenticated = false;
        while (true)
        {
            var frame = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                _logger.LogWarning("Mattermost WebSocket closed by the server");
                SetStatus($"disconnected at {Now():O}; reconnecting");
                return authenticated;
            }

            MattermostFrame parsed;
            try
            {
                parsed = MattermostFrame.Parse(frame);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Ignoring malformed WebSocket frame");
                continue;
            }

            if (parsed.IsReply)
            {
                if (parsed.IsOk)
                {
                    authenticated = true;
                    continue;
                }

                if (parsed.SeqReply == AuthSeq)
                {
                    throw new InvalidOperationException($"Mattermost rejected the bot token: {parsed.Error ?? parsed.Status}");
                }

                _logger.LogDebug("Mattermost action {Seq} failed: {Error}", parsed.SeqReply, parsed.Error);
                continue;
            }

            if (string.Equals(parsed.Event, MattermostFrame.HelloEvent, StringComparison.Ordinal))
            {
                authenticated = true;
                continue;
            }

            if (parsed.Posted is not null)
            {
                await HandlePostedAsync(parsed.Posted, me, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandlePostedAsync(MattermostPostedEvent posted, MattermostUser me, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(posted.ChannelType))
            {
                var channel = await _client.GetChannelAsync(posted.Post.ChannelId, cancellationToken).ConfigureAwait(false);
                posted = posted with { ChannelType = channel?.Type ?? string.Empty, ChannelName = posted.ChannelName ?? channel?.Name };
            }

            if (string.Equals(posted.ChannelType, MattermostChannelTypes.Direct, StringComparison.Ordinal))
            {
                RememberDmChannel(posted.Post.ChannelId);
            }

            var bot = new MattermostBotIdentity(me.Id, me.Username);
            var evt = await _mapper.MapAsync(posted, bot, _users.GetUserAsync, _client.GetThreadAsync, cancellationToken).ConfigureAwait(false);
            if (evt is null)
            {
                return;
            }

            await _queue.EnqueueAsync(evt, cancellationToken).ConfigureAwait(false);
            await _cursors.SetAsync(CursorKey, posted.Post.CreateAt.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Enqueued Mattermost post {PostId} from {User} in {Channel}", posted.Post.Id, evt.Caller.DisplayName, posted.Post.ChannelId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle Mattermost post {PostId}", posted.Post.Id);
        }
    }

    /// <summary>
    /// After a reconnect, logs how long the socket was down and re-reads the DM channels seen in this process
    /// since the last processed post. Channel mentions during the gap are not recovered.
    /// </summary>
    private async Task RecoverGapAsync(MattermostUser me, int attempt, CancellationToken cancellationToken)
    {
        string? cursor;
        try
        {
            cursor = await _cursors.GetAsync(CursorKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the Mattermost cursor");
            return;
        }

        if (cursor is null || !long.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var since))
        {
            if (attempt > 1)
            {
                _logger.LogInformation("Reconnected to Mattermost; no cursor, so posts made while disconnected are not recovered");
            }

            return;
        }

        var lastSeen = DateTimeOffset.FromUnixTimeMilliseconds(since);
        var gap = Now() - lastSeen;
        if (attempt > 1)
        {
            _logger.LogInformation("Reconnected to Mattermost; last processed post was at {LastSeen} ({Gap} ago); re-reading {Count} DM channel(s)", lastSeen, gap, _dmChannels.Count);
        }
        else
        {
            _logger.LogInformation("Mattermost cursor is at {LastSeen} ({Gap} ago); DM channels are only re-read after a reconnect", lastSeen, gap);
            return;
        }

        foreach (var channelId in _dmChannels.Keys.ToArray())
        {
            try
            {
                var posts = await _client.GetPostsForChannelAsync(channelId, since, null, cancellationToken).ConfigureAwait(false);
                foreach (var post in posts.Where(p => p.CreateAt > since).OrderBy(p => p.CreateAt))
                {
                    await HandlePostedAsync(new MattermostPostedEvent(post, MattermostChannelTypes.Direct, []), me, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not re-read DM channel {ChannelId} after reconnect", channelId);
            }
        }
    }

    private void RememberDmChannel(string channelId)
    {
        _dmChannels[channelId] = Now();
        if (_dmChannels.Count <= MaxTrackedDmChannels)
        {
            return;
        }

        foreach (var stale in _dmChannels.OrderBy(kv => kv.Value).Take(_dmChannels.Count - MaxTrackedDmChannels).ToArray())
        {
            _dmChannels.TryRemove(stale.Key, out _);
        }
    }

    private TimeSpan InitialDelay() => TimeSpan.FromSeconds(Math.Max(0, _options.CurrentValue.ReconnectDelaySeconds));

    private TimeSpan NextDelay(TimeSpan current)
    {
        var max = TimeSpan.FromSeconds(Math.Max(0, _options.CurrentValue.MaxReconnectDelaySeconds));
        var doubled = current == TimeSpan.Zero ? InitialDelay() : current * 2;
        return doubled > max ? max : doubled;
    }

    private DateTimeOffset Now() => _time.GetUtcNow();

    private void SetStatus(string status) => _status = status;
}
