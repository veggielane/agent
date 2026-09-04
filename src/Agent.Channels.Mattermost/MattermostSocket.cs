using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Agent.Channels.Mattermost;

/// <summary>A text WebSocket to the Mattermost server. Abstracted so tests can script frames in memory.</summary>
public interface IMattermostSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task SendAsync(string json, CancellationToken cancellationToken);

    /// <summary>The next complete text frame, or null once the peer closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}

public interface IMattermostSocketFactory
{
    IMattermostSocket Create();
}

public sealed class ClientWebSocketMattermostSocketFactory : IMattermostSocketFactory
{
    public IMattermostSocket Create() => new ClientWebSocketMattermostSocket();
}

public sealed class ClientWebSocketMattermostSocket : IMattermostSocket
{
    private const int BufferSize = 32 * 1024;

    private readonly ClientWebSocket _socket = new();

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => _socket.ConnectAsync(uri, cancellationToken);

    public Task SendAsync(string json, CancellationToken cancellationToken)
        => _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await TryCloseOutputAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutting down", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort; the socket is disposed either way.
            }
        }

        _socket.Dispose();
    }

    private async Task TryCloseOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The peer already closed; nothing else to do.
        }
    }
}
