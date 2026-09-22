using System.Text;
using Agent.Core.Authorization;
using Agent.Core.Conversations;
using Agent.Core.Events;
using Agent.Core.Infrastructure;
using Agent.Core.Llm;
using Agent.Core.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agent.Core.Commands.BuiltIn;

/// <summary>Commands every deployment has. Roles follow the plan: Users can look, Team can act on their own work, Admin operates.</summary>
public sealed class BuiltInCommands
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    private readonly ICommandRegistry _registry;
    private readonly IOptionsMonitor<CommandsOptions> _options;

    public BuiltInCommands(ICommandRegistry registry, IOptionsMonitor<CommandsOptions> options)
    {
        _registry = registry;
        _options = options;
    }

    [Command("help", "List commands you can run, or show usage for one", Role = Role.Users)]
    public CommandResult Help(CommandContext ctx, [Arg("Command name")] string? command = null)
    {
        var prefix = _options.CurrentValue.Prefix;
        if (!string.IsNullOrWhiteSpace(command))
        {
            var name = command.TrimStart(prefix.ToCharArray());
            if (!_registry.TryGet(name, out var cmd) || cmd.Hidden)
            {
                return CommandResult.Error($"No command `{prefix}{name}`.");
            }

            return CommandResult.Text(cmd.HelpText(prefix));
        }

        var visible = _registry.All
            .Where(c => !c.Hidden && c.AppliesTo(ctx.Channel) && ctx.Caller.HasRole(c.Role))
            .ToList();

        if (visible.Count == 0)
        {
            return CommandResult.Text("No commands available to you here.");
        }

        var sb = new StringBuilder("**Commands**\n");
        foreach (var group in visible.GroupBy(c => c.Role).OrderBy(g => g.Key))
        {
            sb.Append("\n_").Append(group.Key).AppendLine("_");
            foreach (var c in group)
            {
                sb.Append("- `").Append(c.Usage(prefix)).Append("` — ").AppendLine(c.Description);
            }
        }

        sb.Append("\nUse `").Append(prefix).Append("help <command>` for details.");
        return CommandResult.Text(sb.ToString());
    }

    [Command("ping", "Check the agent is alive", Role = Role.Users)]
    public static string Ping() => "pong";

    [Command("whoami", "Show how the agent sees you: identity, groups, roles", Role = Role.Users)]
    public static string WhoAmI(CommandContext ctx)
    {
        var c = ctx.Caller;
        var sb = new StringBuilder();
        sb.Append("**").Append(c.DisplayName).AppendLine("**");
        sb.Append("- channel: ").Append(c.Channel).Append(", id: `").Append(c.ChannelUserId).AppendLine("`");
        if (c.Username is not null)
        {
            sb.Append("- username: ").AppendLine(c.Username);
        }

        if (c.Email is not null)
        {
            sb.Append("- email: ").AppendLine(c.Email);
        }

        sb.Append("- roles: ").AppendLine(c.Roles.Count == 0 ? "_none_" : string.Join(", ", c.Roles.OrderBy(r => r)));
        sb.Append("- groups: ").AppendLine(c.Groups.Count == 0 ? "_none_" : string.Join(", ", c.Groups.Select(g => $"`{g}`")));
        return sb.ToString().TrimEnd();
    }

    [Command("status", "Queue depth, event sources, active tasks", Role = Role.Users)]
    public static async Task<string> Status(CommandContext ctx, CancellationToken ct)
    {
        var sb = new StringBuilder("**Agent status**\n");
        sb.Append("- uptime: ").AppendLine(Format(DateTimeOffset.UtcNow - StartedAt));

        var queue = ctx.Services.GetService<IInboundQueue>();
        if (queue is not null)
        {
            sb.Append("- inbound queue: ").Append(queue.Depth).AppendLine();
        }

        var tasks = ctx.Services.GetService<ITaskService>();
        if (tasks is not null)
        {
            var active = await tasks.ListAsync(new TaskQuery { ActiveOnly = true, Limit = 100 }, ct).ConfigureAwait(false);
            sb.Append("- active tasks: ").Append(active.Count);
            if (active.Count > 0)
            {
                sb.Append(" (").Append(string.Join(", ", active.GroupBy(t => t.Status).Select(g => $"{g.Count()} {g.Key}"))).Append(')');
            }

            sb.AppendLine();
        }

        foreach (var source in ctx.Services.GetServices<IEventSource>())
        {
            sb.Append("- ").Append(source.Name).Append(": ").AppendLine(source.Status);
        }

        foreach (var contributor in ctx.Services.GetServices<IStatusContributor>())
        {
            string line;
            try
            {
                line = await contributor.GetStatusAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                line = "error: " + ex.Message;
            }

            sb.Append("- ").Append(contributor.Name).Append(": ").AppendLine(line);
        }

        var registry = ctx.Services.GetService<ICommandRegistry>();
        if (registry is { Problems.Count: > 0 })
        {
            sb.Append("- command problems: ").AppendLine(string.Join("; ", registry.Problems));
        }

        return sb.ToString().TrimEnd();
    }

    [Command("tasks", "List coding tasks (yours; --all for everyone's)", Aliases = ["t"], Role = Role.Users)]
    public static async Task<CommandResult> Tasks(
        CommandContext ctx,
        [Option("all", "Everyone's tasks (Team)")] bool all = false,
        [Option("limit", "Maximum number of tasks")] int limit = 10,
        CancellationToken ct = default)
    {
        if (all && !ctx.Caller.HasRole(Role.Team))
        {
            return CommandResult.Error("`--all` needs the Team role.");
        }

        var tasks = ctx.Services.GetRequiredService<ITaskService>();
        var list = await tasks.ListAsync(new TaskQuery { RequesterId = all ? null : ctx.Caller.Key, Limit = Math.Clamp(limit, 1, 100) }, ct).ConfigureAwait(false);
        if (list.Count == 0)
        {
            return CommandResult.Text(all ? "No tasks." : "You have no tasks.");
        }

        var sb = new StringBuilder();
        sb.AppendLine("| # | status | source | title | MR |");
        sb.AppendLine("|---|--------|--------|-------|----|");
        foreach (var t in list)
        {
            sb.Append("| ").Append(t.Id)
              .Append(" | ").Append(t.Status)
              .Append(" | ").Append(t.SourceRef)
              .Append(" | ").Append(Shorten(t.Title ?? t.Instruction, 60).Replace("|", "\\|", StringComparison.Ordinal))
              .Append(" | ").Append(t.MergeRequestUrl ?? "—")
              .AppendLine(" |");
        }

        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    /// <summary>Event types that trace what the coding loop did (commands run, files written); shown on request only.</summary>
    private const string ToolEventPrefix = "tool.";

    [Command("task", "Show one task with its recent events (--actions lists what the coding loop ran and wrote)", Role = Role.Users)]
    public static async Task<CommandResult> Task(
        CommandContext ctx,
        [Arg("Task id")] int id,
        [Option("actions", "Include the recorded tool actions: commands run and files written")] bool actions = false,
        CancellationToken ct = default)
    {
        var tasks = ctx.Services.GetRequiredService<ITaskService>();
        var t = await tasks.GetAsync(id, ct).ConfigureAwait(false);
        if (t is null)
        {
            return CommandResult.Error($"No task #{id}.");
        }

        if (!string.Equals(t.RequesterId, ctx.Caller.Key, StringComparison.OrdinalIgnoreCase) && !ctx.Caller.HasRole(Role.Team))
        {
            return CommandResult.Error("That task belongs to someone else.");
        }

        // Tool actions can outnumber lifecycle events many times over, so the default view leaves them out.
        var recent = await tasks.GetEventsAsync(id, actions ? 60 : 120, ct).ConfigureAwait(false);
        var toolEvents = recent.Count(e => e.Type.StartsWith(ToolEventPrefix, StringComparison.Ordinal));
        var events = actions
            ? recent.TakeLast(40).ToList()
            : recent.Where(e => !e.Type.StartsWith(ToolEventPrefix, StringComparison.Ordinal)).TakeLast(15).ToList();
        var sb = new StringBuilder();
        sb.Append("**Task #").Append(t.Id).Append("** — ").AppendLine(t.Status.ToString());
        sb.Append("- source: ").Append(t.Source).Append(' ').Append(t.SourceRef).Append(t.SourceUrl is null ? string.Empty : $" ({t.SourceUrl})").AppendLine();
        sb.Append("- requester: ").AppendLine(t.RequesterName);
        if (t.RepoUrl is not null)
        {
            sb.Append("- repo: ").Append(t.RepoUrl).Append(t.WorkBranch is null ? string.Empty : $" branch `{t.WorkBranch}`").AppendLine();
        }

        if (t.MergeRequestUrl is not null)
        {
            sb.Append("- merge request: ").AppendLine(t.MergeRequestUrl);
        }

        sb.Append("- turns/tokens: ").Append(t.Turns).Append('/').Append(t.TokensUsed).AppendLine();
        if (!string.IsNullOrWhiteSpace(t.Summary))
        {
            sb.Append("- summary: ").AppendLine(t.Summary);
        }

        if (!string.IsNullOrWhiteSpace(t.Error))
        {
            sb.Append("- error: ").AppendLine(t.Error);
        }

        if (!string.IsNullOrWhiteSpace(t.PendingInstruction))
        {
            sb.Append("- pending follow-up: ").AppendLine(Shorten(t.PendingInstruction, 200));
        }

        if (toolEvents > 0 && !actions)
        {
            sb.Append("- tool actions: ").Append(toolEvents).Append(" recorded (`!task ").Append(t.Id).AppendLine(" --actions` lists them)");
        }

        if (events.Count > 0)
        {
            sb.AppendLine().AppendLine(actions ? "_Recent events and tool actions_" : "_Recent events_");
            foreach (var e in events)
            {
                sb.Append("- ").Append(e.At.ToString("u")).Append(' ').Append(e.Type).Append(": ").AppendLine(e.Message);
            }
        }

        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    [Command("cancel", "Cancel a task (yours; Admin for anyone's)", Role = Role.Team)]
    public static async Task<CommandResult> Cancel(CommandContext ctx, [Arg("Task id")] int id, CancellationToken ct)
    {
        var tasks = ctx.Services.GetRequiredService<ITaskService>();
        var t = await tasks.GetAsync(id, ct).ConfigureAwait(false);
        if (t is null)
        {
            return CommandResult.Error($"No task #{id}.");
        }

        if (!Owns(ctx, t) && !ctx.Caller.HasRole(Role.Admin))
        {
            return CommandResult.Error("Only the requester or an Admin can cancel that task.");
        }

        if (!t.IsActive)
        {
            return CommandResult.Text($"Task #{id} is already {t.Status}.");
        }

        var result = await tasks.CancelAsync(id, ctx.Caller, ct).ConfigureAwait(false);
        return CommandResult.Text($"Task #{id} cancelled ({result?.Status}).");
    }

    [Command("retry", "Re-queue a failed or finished task (yours; Admin for anyone's)", Role = Role.Team)]
    public static async Task<CommandResult> Retry(CommandContext ctx, [Arg("Task id")] int id, CancellationToken ct)
    {
        var tasks = ctx.Services.GetRequiredService<ITaskService>();
        var t = await tasks.GetAsync(id, ct).ConfigureAwait(false);
        if (t is null)
        {
            return CommandResult.Error($"No task #{id}.");
        }

        if (!Owns(ctx, t) && !ctx.Caller.HasRole(Role.Admin))
        {
            return CommandResult.Error("Only the requester or an Admin can retry that task.");
        }

        if (t.IsRunning)
        {
            return CommandResult.Text($"Task #{id} is still running ({t.Status}).");
        }

        await tasks.RetryAsync(id, ctx.Caller, ct).ConfigureAwait(false);
        return CommandResult.Text($"Task #{id} re-queued.");
    }

    [Command("model", "Show or set the model for this conversation (--global for everyone, Admin)", Role = Role.Users)]
    public static CommandResult Model(
        CommandContext ctx,
        [Arg("Model name, or 'reset'")] string? name = null,
        [Option("global", "Change the default for all conversations (Admin)")] bool global = false)
    {
        var settings = ctx.Services.GetRequiredService<IConversationSettings>();
        var factory = ctx.Services.GetRequiredService<IChatClientFactory>();

        if (string.IsNullOrWhiteSpace(name))
        {
            var effective = settings.GetModel(ctx.ConversationId) ?? settings.GlobalModel ?? factory.ResolveModel(ModelPurpose.Answer, $"{ctx.Channel}.Answer");
            var coding = factory.ResolveModel(ModelPurpose.Coding);
            return CommandResult.Text($"answer model: `{effective}`{(settings.GetModel(ctx.ConversationId) is not null ? " (conversation override)" : settings.GlobalModel is not null ? " (global override)" : string.Empty)}\ncoding model: `{coding}`");
        }

        if (global)
        {
            if (!ctx.Caller.HasRole(Role.Admin))
            {
                return CommandResult.Error("`--global` needs the Admin role.");
            }

            settings.GlobalModel = name.Equals("reset", StringComparison.OrdinalIgnoreCase) ? null : name;
            return CommandResult.Text(settings.GlobalModel is null ? "Global model override cleared." : $"Global answer model set to `{settings.GlobalModel}`.");
        }

        if (!ctx.Caller.HasRole(Role.Team))
        {
            return CommandResult.Error("Setting a model needs the Team role.");
        }

        settings.SetModel(ctx.ConversationId, name.Equals("reset", StringComparison.OrdinalIgnoreCase) ? null : name);
        return CommandResult.Text(settings.GetModel(ctx.ConversationId) is null ? "Conversation model override cleared." : $"This conversation now uses `{name}`.");
    }

    [Command("reload", "Re-read command files, MCP definitions, and options", Role = Role.Admin)]
    public static async Task<string> Reload(CommandContext ctx, CancellationToken ct)
    {
        var sb = new StringBuilder("Reloaded:");
        foreach (var r in ctx.Services.GetServices<IReloadable>())
        {
            try
            {
                await r.ReloadAsync(ct).ConfigureAwait(false);
                sb.Append(' ').Append(r.Name).Append(" ✓");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sb.Append(' ').Append(r.Name).Append(" ✗ (").Append(ex.Message).Append(')');
            }
        }

        return sb.ToString();
    }

    private static bool Owns(CommandContext ctx, AgentTask t) => string.Equals(t.RequesterId, ctx.Caller.Key, StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string Format(TimeSpan span)
        => span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{(int)span.TotalMinutes}m {span.Seconds}s";
}
