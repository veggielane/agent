using ModelContextProtocol.Server;

namespace Agent.Mcp.Tests.Support;

internal static class TestTools
{
    public static McpServerTool Echo() => McpServerTool.Create(
        (string text) => "echo: " + text,
        new McpServerToolCreateOptions { Name = "echo", Description = "Echoes text back" });

    public static McpServerTool Slow() => McpServerTool.Create(
        async (int ms, CancellationToken cancellationToken) =>
        {
            await Task.Delay(ms, cancellationToken);
            return "done after " + ms;
        },
        new McpServerToolCreateOptions { Name = "slow", Description = "Waits for ms milliseconds" });

    public static McpServerTool Big() => McpServerTool.Create(
        () => new string('x', 50_000),
        new McpServerToolCreateOptions { Name = "big", Description = "Returns 50k characters" });

    public static McpServerTool Secret() => McpServerTool.Create(
        () => "s3cr3t",
        new McpServerToolCreateOptions { Name = "secret", Description = "Should be hidden" });

    public static McpServerTool Named(string name) => McpServerTool.Create(
        () => "hello from " + name,
        new McpServerToolCreateOptions { Name = name, Description = "Extra tool " + name });

    public static IEnumerable<McpServerTool> All() => [Echo(), Slow(), Big(), Secret()];
}
