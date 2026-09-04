using System.Text;
using System.Text.Json;
using Agent.Core.Audit;
using Agent.Core.Authorization;
using Agent.Core.Infrastructure;
using Agent.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Mcp;

public enum McpServerState
{
    Disabled,
    Connecting,
    Connected,
    Error,
    Invalid,
}

/// <summary>One tool as offered to the model: <see cref="Name"/> is the sanitised function name, <see cref="ToolName"/> the server's.</summary>
public sealed record McpToolInfo(string Name, string ToolName, string? Description, Role Role);

public sealed record McpServerStatus(
    string Name,
    McpServerState State,
    int ToolCount,
    string? Error,
    IReadOnlyList<McpToolInfo> Tools,
    string Transport,
    Role Role,
    ToolScope Scope)
{
    /// <summary>"connected, 4 tools", "error: ...", "disabled", ...</summary>
    public string Summary => State switch
    {
        McpServerState.Connected => $"connected, {ToolCount} tool{(ToolCount == 1 ? string.Empty : "s")}",
        McpServerState.Error => $"error: {Error}",
        McpServerState.Invalid => $"invalid: {Error}",
        McpServerState.Disabled => "disabled",
        _ => "connecting",
    };
}

/// <summary>What the <c>!mcp</c> command needs from the provider.</summary>
public interface IMcpToolProvider
{
    IReadOnlyList<McpServerStatus> Servers { get; }

    /// <summary>Definition problems from the last load (unparseable files, invalid definitions).</summary>
    IReadOnlyList<McpDefinitionProblem> Problems { get; }

    Task ReloadAsync(CancellationToken cancellationToken);

    /// <summary>Invokes a governed tool directly (debugging). Throws <see cref="ArgumentException"/> for bad JSON and <see cref="InvalidOperationException"/> when the server or tool is unknown.</summary>
    Task<string> CallToolAsync(string server, string tool, string jsonArguments, CancellationToken cancellationToken);
}

/// <summary>
/// Connects every enabled MCP server, wraps its tools in <see cref="GovernedAIFunction"/> and offers them through
/// <see cref="IToolSource"/>. Reloads on <c>!reload</c>, retries failed servers in the background, and re-lists tools on
/// <c>tools/list_changed</c>. <see cref="GetTools"/> never touches the network.
/// </summary>
public sealed class McpToolProvider : IMcpToolProvider, IToolSource, IHostedService, IReloadable, IStatusContributor, IAsyncDisposable
{
    private readonly IMcpDefinitionLoader _loader;
    private readonly IMcpClientConnector _connector;
    private readonly IOptionsMonitor<McpOptions> _options;
    private readonly IAuditSink _audit;
    private readonly ILogger<McpToolProvider> _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    private ServerEntry[] _servers = [];
    private ToolDescriptor[] _tools = [];
    private IReadOnlyList<McpDefinitionProblem> _problems = [];
    private CancellationTokenSource? _lifetime;
    private Task? _reconnectLoop;

    public McpToolProvider(
        IMcpDefinitionLoader loader,
        IMcpClientConnector connector,
        IOptionsMonitor<McpOptions> options,
        IAuditSink audit,
        ILogger<McpToolProvider> logger)
    {
        _loader = loader;
        _connector = connector;
        _options = options;
        _audit = audit;
        _logger = logger;
    }

    public string Name => "mcp";

    public IEnumerable<ToolDescriptor> GetTools() => Volatile.Read(ref _tools);

    public IReadOnlyList<McpServerStatus> Servers => Volatile.Read(ref _servers).Select(e => e.Status).ToList();

    public IReadOnlyList<McpDefinitionProblem> Problems => Volatile.Read(ref _problems);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var lifetime = _lifetime ??= new CancellationTokenSource();
        var token = lifetime.Token;
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        if (!token.IsCancellationRequested)
        {
            _reconnectLoop ??= Task.Run(() => ReconnectLoopAsync(token), CancellationToken.None);
        }
    }

    /// <summary>Idempotent: safe to call after a previous stop, and again from <see cref="DisposeAsync"/>.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        var loop = Interlocked.Exchange(ref _reconnectLoop, null);

        if (lifetime is not null)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Host shutdown timed out waiting for the loop; connections are torn down below regardless.
            }
        }

        lifetime?.Dispose();

        var old = Interlocked.Exchange(ref _servers, []);
        Publish();
        await DisposeAllAsync(old).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-reads definitions and connects every enabled server. The previous tool snapshot stays in place until the new
    /// connections are ready, then the old sessions are closed.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await _reloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var set = _loader.Load();
            var options = _options.CurrentValue;
            var entries = set.Servers.Select(d => new ServerEntry(d)).ToList();

            foreach (var group in set.Problems.Where(p => p.Server is not null).GroupBy(p => p.Server!, StringComparer.OrdinalIgnoreCase))
            {
                if (!entries.Any(e => string.Equals(e.Name, group.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    entries.Add(ServerEntry.Invalid(group.Key, string.Join("; ", group.Select(p => p.Message))));
                }
            }

            await Task.WhenAll(entries.Where(e => e.IsEnabled).Select(e => ConnectAsync(e, options, cancellationToken))).ConfigureAwait(false);

            var old = Interlocked.Exchange(ref _servers, entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray());
            Volatile.Write(ref _problems, set.Problems);
            Publish();
            _logger.LogInformation("MCP: {Servers} servers, {Tools} tools, {Problems} definition problems", entries.Count, _tools.Length, set.Problems.Count);

            await DisposeAllAsync(old).ConfigureAwait(false);
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    public Task<string> GetStatusAsync(CancellationToken cancellationToken)
    {
        var servers = Servers;
        var problems = Problems.Count(p => p.Server is null);
        var sb = new StringBuilder();
        sb.Append(servers.Count == 0 ? "no servers" : string.Join("; ", servers.Select(s => $"{s.Name} {s.Summary}")));
        if (problems > 0)
        {
            sb.Append(", ").Append(problems).Append(" unreadable definition file").Append(problems == 1 ? string.Empty : "s");
        }

        return Task.FromResult(sb.ToString());
    }

    public async Task<string> CallToolAsync(string server, string tool, string jsonArguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);

        var entry = Volatile.Read(ref _servers).FirstOrDefault(e => string.Equals(e.Name, server, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No MCP server '{server}'.");

        var snapshot = entry.Current;
        if (snapshot.State != McpServerState.Connected)
        {
            throw new InvalidOperationException($"MCP server '{entry.Name}' is not connected ({entry.Status.Summary}).");
        }

        var match = snapshot.Tools.FirstOrDefault(t =>
                string.Equals(t.Info.Name, tool, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Info.ToolName, tool, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"MCP server '{entry.Name}' has no tool '{tool}'. Try one of: {string.Join(", ", snapshot.Tools.Select(t => t.Info.ToolName))}.");

        var arguments = ParseArguments(jsonArguments);
        var result = await match.Descriptor.Function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        return FormatResult(result);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _reloadLock.Dispose();
    }

    /// <summary>One pass of the reconnect loop: re-dials servers in error and servers whose session has ended.</summary>
    internal async Task ReconnectNowAsync(CancellationToken cancellationToken)
    {
        if (!await _reloadLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return; // a reload is running; it will connect everything anyway
        }

        try
        {
            var options = _options.CurrentValue;
            var candidates = new List<ServerEntry>();
            foreach (var entry in Volatile.Read(ref _servers))
            {
                var current = entry.Current;
                if (!entry.IsEnabled)
                {
                    continue;
                }

                if (current.State == McpServerState.Error)
                {
                    candidates.Add(entry);
                }
                else if (current.State == McpServerState.Connected && current.Connection is { Closed.IsCompleted: true })
                {
                    _logger.LogWarning("MCP server {Server} dropped its session; reconnecting", entry.Name);
                    await entry.DisposeConnectionAsync().ConfigureAwait(false);
                    candidates.Add(entry);
                }
            }

            if (candidates.Count == 0)
            {
                return;
            }

            await Task.WhenAll(candidates.Select(e => ConnectAsync(e, options, cancellationToken))).ConfigureAwait(false);
            Publish();
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    internal static AIFunctionArguments ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AIFunctionArguments();
        }

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(json, McpDefinitionLoader.JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Arguments must be a JSON object: {ex.Message}", nameof(json));
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Arguments must be a JSON object, e.g. {\"query\": \"...\"}.", nameof(json));
        }

        var arguments = new AIFunctionArguments();
        foreach (var property in root.EnumerateObject())
        {
            arguments[property.Name] = property.Value;
        }

        return arguments;
    }

    private async Task ConnectAsync(ServerEntry entry, McpOptions options, CancellationToken cancellationToken)
    {
        var definition = entry.Definition!;
        var timeout = EffectiveTimeout(definition, options);
        IMcpConnection? connection = null;

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            entry.Set(entry.Current with { State = McpServerState.Connecting, Error = null });
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);

            connection = await _connector.ConnectAsync(definition, limit.Token).ConfigureAwait(false);
            var functions = await connection.ListToolsAsync(limit.Token).ConfigureAwait(false);
            var tools = BuildTools(definition, functions, options);

            entry.Set(new EntrySnapshot(McpServerState.Connected, null, connection, tools));
            var captured = connection;
            connection.ToolsChanged += (_, _) => _ = RefreshToolsAsync(entry, captured);
            _logger.LogInformation("MCP server {Server} connected with {Count} tools", entry.Name, tools.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeQuietlyAsync(connection).ConfigureAwait(false);
            entry.Set(new EntrySnapshot(McpServerState.Error, "cancelled", null, []));
            throw;
        }
        catch (OperationCanceledException)
        {
            await DisposeQuietlyAsync(connection).ConfigureAwait(false);
            var message = $"connection timed out after {timeout.TotalSeconds:0}s";
            entry.Set(new EntrySnapshot(McpServerState.Error, message, null, []));
            _logger.LogWarning("MCP server {Server}: {Error}", entry.Name, message);
        }
        catch (Exception ex)
        {
            await DisposeQuietlyAsync(connection).ConfigureAwait(false);
            entry.Set(new EntrySnapshot(McpServerState.Error, ex.Message, null, []));
            _logger.LogWarning(ex, "MCP server {Server} failed to connect", entry.Name);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task RefreshToolsAsync(ServerEntry entry, IMcpConnection connection)
    {
        var cancellationToken = _lifetime?.Token ?? CancellationToken.None;
        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(entry.Current.Connection, connection))
                {
                    return; // the notification came from a session that has since been replaced
                }

                var functions = await connection.ListToolsAsync(cancellationToken).ConfigureAwait(false);
                var tools = BuildTools(entry.Definition!, functions, _options.CurrentValue);
                entry.Set(new EntrySnapshot(McpServerState.Connected, null, connection, tools));
                _logger.LogInformation("MCP server {Server} changed its tool list: {Count} tools", entry.Name, tools.Count);
            }
            finally
            {
                entry.Gate.Release();
            }

            Publish();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MCP server {Server}: re-listing tools failed", entry.Name);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var seconds = Math.Max(1, _options.CurrentValue.ReconnectSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
                await ReconnectNowAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MCP reconnect loop failed; will retry");
            }
        }
    }

    private IReadOnlyList<McpTool> BuildTools(McpServerDefinition definition, IReadOnlyList<AIFunction> functions, McpOptions options)
    {
        var governance = new ToolGovernance
        {
            Timeout = EffectiveTimeout(definition, options),
            MaxResultChars = Math.Max(1, options.MaxResultChars),
        };
        var role = definition.ParsedRole;
        var scope = definition.ParsedScope;
        var channels = definition.ParsedChannels;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tools = new List<McpTool>();

        foreach (var function in functions)
        {
            if (!definition.Tools.IsAllowed(function.Name))
            {
                _logger.LogDebug("MCP tool {Server}/{Tool} filtered out", definition.Name, function.Name);
                continue;
            }

            var name = McpToolNaming.Build(definition.Name, function.Name);
            if (!names.Add(name))
            {
                _logger.LogWarning("MCP tool {Server}/{Tool} ignored: sanitised name {Name} is already taken", definition.Name, function.Name, name);
                continue;
            }

            var toolRole = definition.Tools.RoleFor(function.Name) ?? role;
            var governed = new GovernedAIFunction(function, name, definition.Name, governance, _audit, _logger);
            var descriptor = new ToolDescriptor(governed, toolRole, scope, definition.Name, channels);
            tools.Add(new McpTool(descriptor, new McpToolInfo(name, function.Name, function.Description, toolRole)));
        }

        return tools;
    }

    private void Publish()
    {
        var servers = Volatile.Read(ref _servers);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tools = new List<ToolDescriptor>();
        foreach (var entry in servers)
        {
            foreach (var tool in entry.Current.Tools)
            {
                if (seen.Add(tool.Descriptor.Name))
                {
                    tools.Add(tool.Descriptor);
                }
                else
                {
                    _logger.LogWarning("Duplicate MCP tool name {Name} from server {Server} ignored", tool.Descriptor.Name, entry.Name);
                }
            }
        }

        Volatile.Write(ref _tools, tools.ToArray());
    }

    private static TimeSpan EffectiveTimeout(McpServerDefinition definition, McpOptions options)
        => TimeSpan.FromSeconds(Math.Max(1, definition.TimeoutSeconds ?? options.DefaultTimeoutSeconds));

    private static string FormatResult(object? result)
        => result switch
        {
            null => "(no result)",
            string s => s,
            JsonElement je => je.GetRawText(),
            _ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions),
        };

    private static async Task DisposeAllAsync(IEnumerable<ServerEntry> entries)
    {
        foreach (var entry in entries)
        {
            await entry.DisposeConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeQuietlyAsync(IMcpConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing an MCP connection failed");
        }
    }

    private sealed record McpTool(ToolDescriptor Descriptor, McpToolInfo Info);

    private sealed record EntrySnapshot(McpServerState State, string? Error, IMcpConnection? Connection, IReadOnlyList<McpTool> Tools);

    private sealed class ServerEntry
    {
        private volatile EntrySnapshot _current;

        public ServerEntry(McpServerDefinition definition)
        {
            Definition = definition;
            Name = definition.Name;
            _current = new EntrySnapshot(definition.Enabled ? McpServerState.Connecting : McpServerState.Disabled, null, null, []);
        }

        private ServerEntry(string name, string error)
        {
            Name = name;
            _current = new EntrySnapshot(McpServerState.Invalid, error, null, []);
        }

        public string Name { get; }

        public McpServerDefinition? Definition { get; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool IsEnabled => Definition is { Enabled: true };

        public EntrySnapshot Current => _current;

        public McpServerStatus Status
        {
            get
            {
                var current = _current;
                return new McpServerStatus(
                    Name,
                    current.State,
                    current.Tools.Count,
                    current.Error,
                    current.Tools.Select(t => t.Info).ToList(),
                    Definition?.ParsedTransport.ToString() ?? "-",
                    Definition?.ParsedRole ?? Role.Admin,
                    Definition?.ParsedScope ?? ToolScope.None);
            }
        }

        public static ServerEntry Invalid(string name, string error) => new(name, error);

        public void Set(EntrySnapshot snapshot) => _current = snapshot;

        public async ValueTask DisposeConnectionAsync()
        {
            var current = _current;
            var connection = current.Connection;
            _current = new EntrySnapshot(
                current.State == McpServerState.Connected ? McpServerState.Error : current.State,
                current.State == McpServerState.Connected ? "disconnected" : current.Error,
                null,
                []);

            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best effort: the session is being discarded either way.
                }
            }
        }
    }
}
