using Agent.Core.Authorization;
using Agent.Core.Commands;
using Agent.Core.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Core.Tests.Commands;

public sealed class CommandBinderTests
{
    private readonly IServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private readonly Dictionary<string, CommandDescriptor> _commands;

    public CommandBinderTests()
    {
        _commands = AttributedCommandSource.FromType(typeof(SampleCommands), _services).ToDictionary(c => c.Name);
    }

    private CommandContext Context() => new()
    {
        Caller = CallerIdentity.Local("tester", Role.Users),
        Channel = Core.Channels.Channel.Cli,
        ConversationId = "c",
        Services = _services,
    };

    private async Task<string> RunAsync(string name, string args)
    {
        var cmd = _commands[name];
        var parsed = CommandBinder.Parse(cmd, args);
        var result = await cmd.Invoke(Context(), parsed);
        return result.Markdown;
    }

    [Fact]
    public async Task Positional_And_Flag_And_ValueOption_Bind()
    {
        Assert.Equal("bob", await RunAsync("greet", "bob"));
        Assert.Equal("BOB", await RunAsync("greet", "bob --loud"));
        Assert.Equal("BOB BOB", await RunAsync("greet", "--loud bob --times 2"));
        Assert.Equal("bob bob bob", await RunAsync("greet", "bob --times=3"));
        Assert.Equal("bob bob", await RunAsync("greet", "bob -t 2"));
    }

    [Fact]
    public async Task Rest_CapturesRawRemainder()
    {
        Assert.Equal("hello  \"quoted\" world", await RunAsync("say", "hello  \"quoted\" world"));
        Assert.Equal(string.Empty, await RunAsync("say", string.Empty));
    }

    [Fact]
    public async Task Enum_Converts_CaseInsensitively()
    {
        Assert.Equal("Team", await RunAsync("level", "team"));
    }

    [Fact]
    public async Task Optional_Positional_DefaultsWhenMissing()
    {
        Assert.Equal("(none)", await RunAsync("optional", string.Empty));
        Assert.Equal("x", await RunAsync("optional", "x"));
    }

    [Fact]
    public async Task Context_And_CancellationToken_AreInjected()
    {
        Assert.Equal("tester", await RunAsync("ctx", string.Empty));
    }

    [Fact]
    public async Task AsyncResult_IsAwaited()
    {
        Assert.Equal("async", await RunAsync("asyncy", string.Empty));
    }

    [Fact]
    public void MissingRequired_Throws()
    {
        var cmd = _commands["greet"];
        var parsed = CommandBinder.Parse(cmd, string.Empty);
        var ex = Assert.ThrowsAsync<CommandBindingException>(() => cmd.Invoke(Context(), parsed));
        Assert.Contains("name", ex.Result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownOption_Throws()
    {
        var ex = Assert.Throws<CommandBindingException>(() => CommandBinder.Parse(_commands["greet"], "bob --nope"));
        Assert.Contains("--nope", ex.Message);
    }

    [Fact]
    public void OptionWithoutValue_Throws()
    {
        Assert.Throws<CommandBindingException>(() => CommandBinder.Parse(_commands["greet"], "bob --times"));
    }

    [Fact]
    public async Task InvalidInt_Throws()
    {
        var cmd = _commands["greet"];
        var parsed = CommandBinder.Parse(cmd, "bob --times lots");
        await Assert.ThrowsAsync<CommandBindingException>(() => cmd.Invoke(Context(), parsed));
    }

    [Fact]
    public async Task TooManyPositionals_Throws()
    {
        var cmd = _commands["greet"];
        var parsed = CommandBinder.Parse(cmd, "bob extra");
        await Assert.ThrowsAsync<CommandBindingException>(() => cmd.Invoke(Context(), parsed));
    }

    [Fact]
    public void Usage_ReflectsParameters()
    {
        Assert.Equal("!greet <name> [--loud] [--times <value>]", _commands["greet"].Usage());
        Assert.Equal("!say [text]...", _commands["say"].Usage());
        Assert.Contains("aliases: !to", _commands["teamonly"].HelpText());
    }

    [Fact]
    public void Descriptor_WithoutRole_Throws()
    {
        Assert.Throws<CommandDefinitionException>(() => AttributedCommandSource.FromType(typeof(NoRoleCommands), _services).ToList());
    }

    [Fact]
    public void Convert_HandlesHashPrefixedIds()
    {
        Assert.Equal(42, CommandBinder.Convert("#42", typeof(int), "id"));
        Assert.Equal(true, CommandBinder.Convert("yes", typeof(bool), "flag"));
    }
}
