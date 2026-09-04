using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Agent.Mcp;

/// <summary>Connects with the official SDK: <see cref="StdioClientTransport"/> or <see cref="HttpClientTransport"/>.</summary>
public sealed class SdkMcpClientConnector : IMcpClientConnector
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SdkMcpClientConnector> _logger;

    public SdkMcpClientConnector(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<SdkMcpClientConnector>();
    }

    public async Task<IMcpConnection> ConnectAsync(McpServerDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var transport = CreateTransport(definition);
        return await SdkMcpConnection.CreateAsync(transport, _loggerFactory, cancellationToken).ConfigureAwait(false);
    }

    public IClientTransport CreateTransport(McpServerDefinition definition)
        => definition.ParsedTransport switch
        {
            McpTransportKind.Http => new HttpClientTransport(ToHttpOptions(definition), _loggerFactory),
            _ => new StdioClientTransport(ToStdioOptions(definition, _logger), _loggerFactory),
        };

    /// <summary>
    /// Stdio: the child gets the SDK's minimal safe environment (PATH, HOME, system directories) plus the definition's
    /// <see cref="McpServerDefinition.Env"/>, unless <see cref="McpServerDefinition.InheritEnvironment"/> is set.
    /// </summary>
    public static StdioClientTransportOptions ToStdioOptions(McpServerDefinition definition, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Command))
        {
            throw new InvalidOperationException($"MCP server '{definition.Name}' has no Command.");
        }

        var env = definition.InheritEnvironment
            ? new Dictionary<string, string?>()
            : StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var (key, value) in definition.Env)
        {
            env[key] = value;
        }

        var name = definition.Name;
        return new StdioClientTransportOptions
        {
            Name = name,
            Command = definition.Command,
            Arguments = definition.Args.ToList(),
            WorkingDirectory = string.IsNullOrWhiteSpace(definition.WorkingDirectory) ? null : Path.GetFullPath(definition.WorkingDirectory),
            InheritEnvironmentVariables = definition.InheritEnvironment,
            EnvironmentVariables = env,
            StandardErrorLines = logger is null ? null : line => logger.LogDebug("[mcp:{Server}] {Line}", name, line),
        };
    }

    public static HttpClientTransportOptions ToHttpOptions(McpServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Url))
        {
            throw new InvalidOperationException($"MCP server '{definition.Name}' has no Url.");
        }

        return new HttpClientTransportOptions
        {
            Name = definition.Name,
            Endpoint = new Uri(definition.Url, UriKind.Absolute),
            TransportMode = HttpTransportMode.AutoDetect,
            AdditionalHeaders = definition.Headers.Count == 0 ? null : new Dictionary<string, string>(definition.Headers),
        };
    }
}

/// <summary>An <see cref="IMcpConnection"/> over an SDK <see cref="McpClient"/>. Also usable with custom transports (tests).</summary>
public sealed class SdkMcpConnection : IMcpConnection
{
    private static readonly string ClientVersion =
        typeof(SdkMcpConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0";

    private readonly IAsyncDisposable _notificationRegistration;

    private SdkMcpConnection(McpClient client)
    {
        Client = client;
        Closed = client.Completion.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _notificationRegistration = client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (_, _) =>
            {
                ToolsChanged?.Invoke(this, EventArgs.Empty);
                return default;
            });
    }

    public McpClient Client { get; }

    public event EventHandler? ToolsChanged;

    public Task Closed { get; }

    public static async Task<SdkMcpConnection> CreateAsync(IClientTransport transport, ILoggerFactory? loggerFactory = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var options = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "agent", Version = ClientVersion },
        };
        var client = await McpClient.CreateAsync(transport, options, loggerFactory, cancellationToken).ConfigureAwait(false);
        return new SdkMcpConnection(client);
    }

    public async Task<IReadOnlyList<AIFunction>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = await Client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return tools.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _notificationRegistration.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The session may already be gone; disposal of the client below is what matters.
        }

        await Client.DisposeAsync().ConfigureAwait(false);
    }
}
