using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Agent.Mcp.Tests.Support;

/// <summary>Starts a fresh in-process SDK server for every connect and wires the SDK client to it over pipes.</summary>
internal sealed class InProcessConnector : IMcpClientConnector
{
    private readonly List<InProcessMcpServer> _servers = [];

    public Func<McpServerDefinition, IEnumerable<McpServerTool>> ToolsFor { get; set; } = _ => TestTools.All();

    /// <summary>Return an exception to make the next connect for that definition fail.</summary>
    public Func<McpServerDefinition, Exception?> FailWith { get; set; } = _ => null;

    public int ConnectCount { get; private set; }

    public IReadOnlyList<InProcessMcpServer> Servers => _servers;

    public InProcessMcpServer LastServer => _servers[^1];

    public async Task<IMcpConnection> ConnectAsync(McpServerDefinition definition, CancellationToken cancellationToken)
    {
        ConnectCount++;
        if (FailWith(definition) is { } failure)
        {
            throw failure;
        }

        var server = new InProcessMcpServer(ToolsFor(definition), definition.Name);
        _servers.Add(server);
        var connection = await SdkMcpConnection.CreateAsync(server.ClientTransport, cancellationToken: cancellationToken);
        return new OwningConnection(connection, server);
    }

    public async ValueTask DisposeAllAsync()
    {
        foreach (var server in _servers)
        {
            await server.DisposeAsync();
        }
    }

    /// <summary>Tears the server down together with the client session so a reload leaves nothing running.</summary>
    private sealed class OwningConnection : IMcpConnection
    {
        private readonly SdkMcpConnection _inner;
        private readonly InProcessMcpServer _server;

        public OwningConnection(SdkMcpConnection inner, InProcessMcpServer server)
        {
            _inner = inner;
            _server = server;
            _inner.ToolsChanged += (s, e) => ToolsChanged?.Invoke(this, e);
        }

        public event EventHandler? ToolsChanged;

        public Task Closed => _inner.Closed;

        public Task<IReadOnlyList<AIFunction>> ListToolsAsync(CancellationToken cancellationToken) => _inner.ListToolsAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await _server.DisposeAsync();
        }
    }
}
