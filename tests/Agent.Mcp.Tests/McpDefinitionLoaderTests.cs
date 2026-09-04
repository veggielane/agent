using Agent.Core.Authorization;
using Agent.Core.Tools;
using Agent.Mcp.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Mcp.Tests;

public sealed class McpDefinitionLoaderTests
{
    private static McpDefinitionLoader Loader(McpOptions? options = null)
        => new(new TestOptionsMonitor<McpOptions>(options ?? new McpOptions()), NullLogger<McpDefinitionLoader>.Instance);

    [Fact]
    public void Load_OptionsOnly_FillsNameFromDictionaryKey()
    {
        var options = new McpOptions { Directory = string.Empty };
        options.Servers["inventory"] = new McpServerDefinition { Transport = "Http", Url = "https://mcp.internal/inventory" };
        options.Servers["named"] = new McpServerDefinition { Name = "explicit", Command = "x" };

        var set = Loader(options).Load();

        Assert.Empty(set.Problems);
        Assert.Equal(["explicit", "inventory"], set.Servers.Select(s => s.Name));
        Assert.Equal(McpTransportKind.Http, set.Servers[1].ParsedTransport);
    }

    [Fact]
    public void Load_FileOverridesOptionsEntryWithSameName()
    {
        using var dir = new TempDirectory();
        dir.Write("docs.json", """{ "Name": "docs", "Transport": "Stdio", "Command": "from-file", "Role": "Users" }""");
        var options = new McpOptions { Directory = dir.Path };
        options.Servers["docs"] = new McpServerDefinition { Command = "from-options", Role = "Admin" };

        var set = Loader(options).Load();

        var docs = Assert.Single(set.Servers);
        Assert.Equal("from-file", docs.Command);
        Assert.Equal(Role.Users, docs.ParsedRole);
    }

    [Fact]
    public void Load_FileWithoutName_UsesFileName()
    {
        using var dir = new TempDirectory();
        dir.Write("wiki.json", """{ "command": "npx", "args": ["-y", "@team/wiki"], "env": { "WIKI_ROOT": "D:\\wiki" }, "scope": ["answer"] }""");

        var set = Loader(new McpOptions { Directory = dir.Path }).Load();

        var wiki = Assert.Single(set.Servers);
        Assert.Equal("wiki", wiki.Name);
        Assert.Equal(["-y", "@team/wiki"], wiki.Args);
        Assert.Equal("D:\\wiki", wiki.Env["WIKI_ROOT"]);
        Assert.Equal(ToolScope.Answer, wiki.ParsedScope);
    }

    [Fact]
    public void Load_InvalidJson_SkippedWithProblem()
    {
        using var dir = new TempDirectory();
        dir.Write("broken.json", "{ this is not json");
        dir.Write("ok.json", """{ "Command": "x" }""");

        var set = Loader(new McpOptions { Directory = dir.Path }).Load();

        Assert.Equal("ok", Assert.Single(set.Servers).Name);
        var problem = Assert.Single(set.Problems);
        Assert.Null(problem.Server);
        Assert.EndsWith("broken.json", problem.File);
        Assert.Contains("broken.json", problem.ToString());
    }

    [Fact]
    public void Load_InvalidDefinition_ExcludedWithProblem()
    {
        using var dir = new TempDirectory();
        dir.Write("bad.json", """{ "Transport": "Http", "Role": "Owner" }""");

        var set = Loader(new McpOptions { Directory = dir.Path }).Load();

        Assert.Empty(set.Servers);
        Assert.Equal(2, set.Problems.Count);
        Assert.All(set.Problems, p => Assert.Equal("bad", p.Server));
        Assert.Contains(set.Problems, p => p.Message.Contains("Url", StringComparison.Ordinal));
        Assert.Contains(set.Problems, p => p.Message.Contains("Owner", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_AcceptsCommentsTrailingCommasAndAnyCasing()
    {
        using var dir = new TempDirectory();
        dir.Write("docs.json", """
            {
              // launched via npx
              "transport": "stdio",
              "COMMAND": "npx",
              "tools": { "allow": ["search*", "read*"], "deny": [], "roles": { "read_private*": "Team" }, },
              "timeoutSeconds": 45,
            }
            """);

        var set = Loader(new McpOptions { Directory = dir.Path }).Load();

        Assert.Empty(set.Problems);
        var docs = Assert.Single(set.Servers);
        Assert.Equal("npx", docs.Command);
        Assert.Equal(["search*", "read*"], docs.Tools.Allow);
        Assert.Equal("Team", docs.Tools.Roles["read_private*"]);
        Assert.Equal(45, docs.TimeoutSeconds);
    }

    [Fact]
    public void Load_MissingDirectory_ReturnsOptionsOnly()
    {
        var options = new McpOptions { Directory = Path.Combine(Path.GetTempPath(), "agent-mcp-does-not-exist-" + Guid.NewGuid().ToString("N")) };
        options.Servers["docs"] = new McpServerDefinition { Command = "x" };

        var set = Loader(options).Load();

        Assert.Empty(set.Problems);
        Assert.Equal("docs", Assert.Single(set.Servers).Name);
    }

    [Fact]
    public void Load_IgnoresNonJsonFiles()
    {
        using var dir = new TempDirectory();
        dir.Write("notes.txt", "not a definition");
        dir.Write("docs.json", """{ "Command": "x" }""");

        var set = Loader(new McpOptions { Directory = dir.Path }).Load();

        Assert.Single(set.Servers);
        Assert.Empty(set.Problems);
    }
}
