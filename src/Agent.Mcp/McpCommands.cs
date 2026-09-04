using System.Text;
using Agent.Core.Authorization;
using Agent.Core.Commands;
using Agent.Core.Pipeline;
using Agent.Core.Tools;

namespace Agent.Mcp;

/// <summary><c>!mcp list | tools &lt;server&gt; | reload | call &lt;server&gt; &lt;tool&gt; &lt;json&gt;</c>. Team may look; Admin may reload and call.</summary>
public sealed class McpCommands
{
    private readonly IMcpToolProvider _provider;

    public McpCommands(IMcpToolProvider provider) => _provider = provider;

    [Command("mcp", "MCP servers: list | tools <server> | reload | call <server> <tool> <json>", Role = Role.Team)]
    public async Task<CommandResult> Mcp(
        CommandContext ctx,
        [Arg("list | tools | reload | call")] string action,
        [Arg("Server name")] string? server = null,
        [Arg("Tool name")] string? tool = null,
        [Rest] string json = "")
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "list":
            case "ls":
                return CommandResult.Text(List());

            case "tools":
                return Tools(server);

            case "reload":
                if (!ctx.Caller.HasRole(Role.Admin))
                {
                    return CommandResult.Error("`mcp reload` needs the Admin role.");
                }

                await _provider.ReloadAsync(ctx.CancellationToken).ConfigureAwait(false);
                return CommandResult.Text("MCP servers reloaded.\n\n" + List());

            case "call":
                if (!ctx.Caller.HasRole(Role.Admin))
                {
                    return CommandResult.Error("`mcp call` needs the Admin role.");
                }

                return await CallAsync(ctx, server, tool, json).ConfigureAwait(false);

            default:
                return CommandResult.Error($"Unknown action `{action}`. Use `list`, `tools <server>`, `reload`, or `call <server> <tool> <json>`.");
        }
    }

    private string List()
    {
        var servers = _provider.Servers;
        var problems = _provider.Problems;
        var sb = new StringBuilder();

        if (servers.Count == 0)
        {
            sb.AppendLine("No MCP servers configured.");
        }
        else
        {
            sb.AppendLine("| server | state | tools | role | scope | transport | detail |");
            sb.AppendLine("|--------|-------|-------|------|-------|-----------|--------|");
            foreach (var s in servers)
            {
                sb.Append("| ").Append(Cell(s.Name))
                  .Append(" | ").Append(s.State.ToString().ToLowerInvariant())
                  .Append(" | ").Append(s.ToolCount)
                  .Append(" | ").Append(s.Role)
                  .Append(" | ").Append(Scope(s.Scope))
                  .Append(" | ").Append(s.Transport)
                  .Append(" | ").Append(Cell(s.Error ?? "-"))
                  .AppendLine(" |");
            }
        }

        var files = problems.Where(p => p.Server is null).ToList();
        if (files.Count > 0)
        {
            sb.AppendLine().AppendLine("**Definition problems**");
            foreach (var p in files)
            {
                sb.Append("- ").AppendLine(p.ToString());
            }
        }

        return sb.ToString().TrimEnd();
    }

    private CommandResult Tools(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return CommandResult.Error("Usage: `mcp tools <server>`.");
        }

        var status = _provider.Servers.FirstOrDefault(s => string.Equals(s.Name, server, StringComparison.OrdinalIgnoreCase));
        if (status is null)
        {
            return CommandResult.Error($"No MCP server `{server}`. Use `mcp list`.");
        }

        var sb = new StringBuilder();
        sb.Append("**").Append(status.Name).Append("** - ").AppendLine(status.Summary);
        if (status.Tools.Count == 0)
        {
            return CommandResult.Text(sb.ToString().TrimEnd());
        }

        sb.AppendLine();
        sb.AppendLine("| tool | role | description |");
        sb.AppendLine("|------|------|-------------|");
        foreach (var t in status.Tools)
        {
            sb.Append("| `").Append(t.Name).Append('`')
              .Append(" | ").Append(t.Role)
              .Append(" | ").Append(Cell(Shorten(t.Description ?? string.Empty, 120)))
              .AppendLine(" |");
        }

        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    private async Task<CommandResult> CallAsync(CommandContext ctx, string? server, string? tool, string json)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(tool))
        {
            return CommandResult.Error("Usage: `mcp call <server> <tool> {\"arg\": \"value\"}`.");
        }

        try
        {
            using var scope = RequestContext.Begin(ctx.Caller, ctx.Event);
            var result = await _provider.CallToolAsync(server, tool, json, ctx.CancellationToken).ConfigureAwait(false);
            return CommandResult.Text($"```\n{result}\n```");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return CommandResult.Error(ex.Message);
        }
    }

    private static string Scope(ToolScope scope)
        => scope == ToolScope.All ? "Answer, Coding" : scope == ToolScope.None ? "-" : scope.ToString();

    private static string Cell(string s) => s.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
