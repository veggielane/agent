using Agent.Core.Audit;
using Agent.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Mcp.Tests.Support;

/// <summary>A started <see cref="McpToolProvider"/> with its fakes, torn down after the test.</summary>
internal sealed class ProviderHost : IAsyncDisposable
{
    private ProviderHost(McpToolProvider provider, InProcessConnector connector, InMemoryAuditSink audit, McpOptions options, TempDirectory? directory)
    {
        Provider = provider;
        Connector = connector;
        Audit = audit;
        Options = options;
        Directory = directory;
    }

    public McpToolProvider Provider { get; }

    public InProcessConnector Connector { get; }

    public InMemoryAuditSink Audit { get; }

    public McpOptions Options { get; }

    public TempDirectory? Directory { get; }

    public static McpServerDefinition Docs(Action<McpServerDefinition>? configure = null)
    {
        var def = new McpServerDefinition { Name = "docs", Transport = "Stdio", Command = "fake-server" };
        configure?.Invoke(def);
        return def;
    }

    /// <summary>Builds and starts a provider for the given inline definitions (no drop-in directory).</summary>
    public static Task<ProviderHost> StartAsync(params McpServerDefinition[] definitions)
        => StartAsync(o => { }, null, definitions);

    public static async Task<ProviderHost> StartAsync(Action<McpOptions> configure, InProcessConnector? connector = null, params McpServerDefinition[] definitions)
    {
        var options = new McpOptions { Directory = string.Empty };
        foreach (var def in definitions)
        {
            options.Servers[def.Name] = def;
        }

        configure(options);
        TempDirectory? directory = null;
        if (options.Directory == "temp")
        {
            directory = new TempDirectory();
            options.Directory = directory.Path;
        }

        connector ??= new InProcessConnector();
        var audit = new InMemoryAuditSink();
        var monitor = new TestOptionsMonitor<McpOptions>(options);
        var loader = new McpDefinitionLoader(monitor, NullLogger<McpDefinitionLoader>.Instance);
        var provider = new McpToolProvider(loader, connector, monitor, audit, NullLogger<McpToolProvider>.Instance);
        await provider.StartAsync(TestContext.Current.CancellationToken);
        return new ProviderHost(provider, connector, audit, options, directory);
    }

    public ToolDescriptor Tool(string name)
        => Provider.GetTools().Single(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public McpServerStatus Status(string server)
        => Provider.Servers.Single(s => string.Equals(s.Name, server, StringComparison.OrdinalIgnoreCase));

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        await Connector.DisposeAllAsync();
        Directory?.Dispose();
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agent-mcp-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string fileName, string content)
    {
        var file = System.IO.Path.Combine(Path, fileName);
        File.WriteAllText(file, content);
        return file;
    }

    public void Delete(string fileName) => File.Delete(System.IO.Path.Combine(Path, fileName));

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
