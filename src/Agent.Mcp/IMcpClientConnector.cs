using Microsoft.Extensions.AI;

namespace Agent.Mcp;

/// <summary>A live session with one MCP server.</summary>
public interface IMcpConnection : IAsyncDisposable
{
    /// <summary>The server's tools as callable functions (the SDK's <c>McpClientTool</c> in production).</summary>
    Task<IReadOnlyList<AIFunction>> ListToolsAsync(CancellationToken cancellationToken);

    /// <summary>Raised when the server sends <c>notifications/tools/list_changed</c>. Handlers must not block.</summary>
    event EventHandler? ToolsChanged;

    /// <summary>Completes when the session ends for any reason (server exit, transport closed, disposal).</summary>
    Task Closed { get; }
}

/// <summary>Opens connections. Production uses the SDK transports; tests connect to an in-process server.</summary>
public interface IMcpClientConnector
{
    Task<IMcpConnection> ConnectAsync(McpServerDefinition definition, CancellationToken cancellationToken);
}
