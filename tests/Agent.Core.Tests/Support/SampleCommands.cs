using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Commands;

namespace Agent.Core.Tests.Support;

public sealed class SampleCommands
{
    [Command("greet", "Greets someone", Role = Role.Users)]
    public string Greet([Arg("Name to greet")] string name, [Option("loud", "Shout it")] bool loud = false, [Option("times", "Repeat count", Short = 't')] int times = 1)
        => string.Join(" ", Enumerable.Repeat(loud ? name.ToUpperInvariant() : name, times));

    [Command("say", "Echoes the rest of the line", Role = Role.Users)]
    public string Say([Rest] string text) => text;

    [Command("level", "Enum argument", Role = Role.Users)]
    public string Level(Role role) => role.ToString();

    [Command("teamonly", "Needs Team", Role = Role.Team, Aliases = ["to"])]
    public string TeamOnly() => "team";

    [Command("adminonly", "Needs Admin", Role = Role.Admin)]
    public string AdminOnly() => "admin";

    [Command("boom", "Throws", Role = Role.Users)]
    public string Boom() => throw new InvalidOperationException("kaboom");

    [Command("ctx", "Uses the context", Role = Role.Users)]
    public string Ctx(CommandContext ctx, CancellationToken ct) => ctx.Caller.DisplayName;

    [Command("mmonly", "Mattermost only", Role = Role.Users, Channels = [Channel.Mattermost])]
    public string MmOnly() => "mm";

    [Command("tool", "Exposed as a tool", Role = Role.Users, ExposeAsTool = true)]
    public string Tool([Rest] string text = "") => "tool:" + text;

    [Command("asyncy", "Async result", Role = Role.Users)]
    public async Task<CommandResult> Asyncy()
    {
        await Task.Yield();
        return CommandResult.Text("async");
    }

    [Command("optional", "Optional positional", Role = Role.Users)]
    public string Optional(string? what = null) => what ?? "(none)";
}

public sealed class NoRoleCommands
{
    [Command("norole", "Missing role")]
    public string NoRole() => "x";
}

public sealed class DuplicateCommands
{
    [Command("greet", "Duplicate of greet", Role = Role.Users)]
    public string Greet() => "dup";
}
