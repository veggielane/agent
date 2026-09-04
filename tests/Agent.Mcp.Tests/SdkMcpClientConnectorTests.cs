using ModelContextProtocol.Client;

namespace Agent.Mcp.Tests;

public sealed class SdkMcpClientConnectorTests
{
    [Fact]
    public void ToStdioOptions_MapsCommandArgsEnvAndWorkingDirectory()
    {
        var def = new McpServerDefinition
        {
            Name = "docs",
            Command = "npx",
            Args = ["-y", "@team/docs-mcp"],
            Env = { ["DOCS_ROOT"] = "D:\\docs" },
            WorkingDirectory = ".",
        };

        var options = SdkMcpClientConnector.ToStdioOptions(def);

        Assert.Equal("docs", options.Name);
        Assert.Equal("npx", options.Command);
        Assert.Equal(["-y", "@team/docs-mcp"], options.Arguments);
        Assert.Equal(Path.GetFullPath("."), options.WorkingDirectory);
        Assert.False(options.InheritEnvironmentVariables);
        Assert.NotNull(options.EnvironmentVariables);
        Assert.Equal("D:\\docs", options.EnvironmentVariables["DOCS_ROOT"]);
        Assert.DoesNotContain("SOME_SECRET_TOKEN", options.EnvironmentVariables.Keys);
    }

    [Fact]
    public void ToStdioOptions_MinimalEnvironment_ForwardsPathButNotEverything()
    {
        Environment.SetEnvironmentVariable("AGENT_MCP_TEST_SECRET", "hidden");
        try
        {
            var options = SdkMcpClientConnector.ToStdioOptions(new McpServerDefinition { Name = "docs", Command = "x" });

            Assert.NotNull(options.EnvironmentVariables);
            Assert.DoesNotContain("AGENT_MCP_TEST_SECRET", options.EnvironmentVariables.Keys);
            Assert.Contains(options.EnvironmentVariables.Keys, k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_MCP_TEST_SECRET", null);
        }
    }

    [Fact]
    public void ToStdioOptions_InheritEnvironment_PassesOnlyDefinitionEnvOnTop()
    {
        var def = new McpServerDefinition { Name = "docs", Command = "x", InheritEnvironment = true, Env = { ["A"] = "1" } };

        var options = SdkMcpClientConnector.ToStdioOptions(def);

        Assert.True(options.InheritEnvironmentVariables);
        Assert.Equal(new Dictionary<string, string?> { ["A"] = "1" }, options.EnvironmentVariables);
    }

    [Fact]
    public void ToStdioOptions_WithoutCommand_Throws()
        => Assert.Throws<InvalidOperationException>(() => SdkMcpClientConnector.ToStdioOptions(new McpServerDefinition { Name = "docs" }));

    [Fact]
    public void ToHttpOptions_MapsUrlAndHeaders()
    {
        var def = new McpServerDefinition
        {
            Name = "inventory",
            Transport = "Http",
            Url = "https://mcp.internal/inventory",
            Headers = { ["Authorization"] = "Bearer secret", ["X-Team"] = "ops" },
        };

        var options = SdkMcpClientConnector.ToHttpOptions(def);

        Assert.Equal("inventory", options.Name);
        Assert.Equal(new Uri("https://mcp.internal/inventory"), options.Endpoint);
        Assert.Equal(HttpTransportMode.AutoDetect, options.TransportMode);
        Assert.NotNull(options.AdditionalHeaders);
        Assert.Equal("Bearer secret", options.AdditionalHeaders["Authorization"]);
        Assert.Equal("ops", options.AdditionalHeaders["X-Team"]);
    }

    [Fact]
    public void ToHttpOptions_NoHeaders_LeavesAdditionalHeadersNull()
    {
        var options = SdkMcpClientConnector.ToHttpOptions(new McpServerDefinition { Name = "inv", Transport = "Http", Url = "http://localhost:5080/mcp" });

        Assert.Null(options.AdditionalHeaders);
    }

    [Fact]
    public void ToHttpOptions_WithoutUrl_Throws()
        => Assert.Throws<InvalidOperationException>(() => SdkMcpClientConnector.ToHttpOptions(new McpServerDefinition { Name = "inv", Transport = "Http" }));

    [Fact]
    public void CreateTransport_PicksSdkTransportByKind()
    {
        var connector = new SdkMcpClientConnector();

        Assert.IsType<StdioClientTransport>(connector.CreateTransport(new McpServerDefinition { Name = "a", Command = "x" }));
        Assert.IsType<HttpClientTransport>(connector.CreateTransport(new McpServerDefinition { Name = "b", Transport = "http", Url = "https://mcp.internal/" }));
    }
}
