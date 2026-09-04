using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agent.Mcp.Tests.Support;

/// <summary>A real SDK <see cref="McpServer"/> running in-process; the client side talks to it over two pipes.</summary>
internal sealed class InProcessMcpServer : IAsyncDisposable
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _run;

    public InProcessMcpServer(IEnumerable<McpServerTool> tools, string name = "test")
    {
        Tools = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in tools)
        {
            Tools.Add(tool);
        }

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = name, Version = "1.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } },
            ToolCollection = Tools,
        };

        var transport = new StreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream(), name);
        Server = McpServer.Create(transport, options);
        _run = Server.RunAsync(_cts.Token);
        ClientTransport = new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream());
    }

    public McpServerPrimitiveCollection<McpServerTool> Tools { get; }

    public McpServer Server { get; }

    public IClientTransport ClientTransport { get; }

    public bool IsStopped => _run.IsCompleted;

    /// <summary>Adds a tool and announces it, as a real server would (the SDK does not send the notification by itself).</summary>
    public async Task AddToolAsync(McpServerTool tool, CancellationToken cancellationToken)
    {
        Tools.Add(tool);
        await Server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, cancellationToken);
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cts.CancelAsync();
        _clientToServer.Writer.Complete();
        _serverToClient.Writer.Complete();
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // cancelled, faulted or timed out: the session is being discarded either way
        }

        await Server.DisposeAsync();
        _cts.Dispose();
    }
}
