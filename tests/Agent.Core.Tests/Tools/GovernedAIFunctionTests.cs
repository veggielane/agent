using Agent.Core.Audit;
using Agent.Core.Authorization;
using Agent.Core.Pipeline;
using Agent.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Core.Tests.Tools;

public sealed class GovernedAIFunctionTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static (GovernedAIFunction Function, InMemoryAuditSink Audit) Wrap(Delegate impl, string name, ToolGovernance? governance = null)
    {
        var audit = new InMemoryAuditSink();
        var inner = AIFunctionFactory.Create(impl, "inner", "test");
        var fn = new GovernedAIFunction(inner, name, "unit", governance ?? new ToolGovernance(), audit, NullLogger.Instance);
        return (fn, audit);
    }

    [Fact]
    public async Task Invoke_RenamesAudits_AndPassesThrough()
    {
        var (fn, audit) = Wrap((string text) => "got " + text, "srv__echo");
        using var scope = RequestContext.Begin(CallerIdentity.Local("alice", Role.Users));

        var result = await fn.InvokeAsync(new AIFunctionArguments { ["text"] = "x" }, Ct);

        Assert.Equal("srv__echo", fn.Name);
        Assert.Equal("got x", result?.ToString());
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("tool:srv__echo", entry.Action);
        Assert.Equal("ok", entry.Outcome);
        Assert.Equal("alice", entry.CallerId);
        Assert.Contains("source=unit", entry.Detail);
    }

    [Fact]
    public async Task Invoke_TruncatesLargeResults()
    {
        var (fn, _) = Wrap(() => new string('x', 500), "big", new ToolGovernance { MaxResultChars = 100 });
        var result = (await fn.InvokeAsync(new AIFunctionArguments(), Ct))?.ToString();

        Assert.NotNull(result);
        Assert.StartsWith(new string('x', 100), result);
        Assert.Contains("[truncated 400 characters]", result);
    }

    [Fact]
    public async Task Invoke_TimesOut_ReturnsMessage()
    {
        var (fn, audit) = Wrap(async (CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return "late";
        }, "slow", new ToolGovernance { Timeout = TimeSpan.FromMilliseconds(100) });

        var result = (await fn.InvokeAsync(new AIFunctionArguments(), Ct))?.ToString();

        Assert.Contains("timed out", result);
        Assert.Equal("timeout", audit.Entries.Single().Outcome);
    }

    [Fact]
    public async Task Invoke_Exception_ReturnsErrorText()
    {
        var (fn, audit) = Wrap(new Func<string>(() => throw new InvalidOperationException("nope")), "bad");
        var result = (await fn.InvokeAsync(new AIFunctionArguments(), Ct))?.ToString();

        Assert.Equal("Tool 'bad' failed: nope", result);
        Assert.StartsWith("error:", audit.Entries.Single().Outcome);
    }

    [Fact]
    public async Task Invoke_ArgumentsTooLarge_Throws()
    {
        var (fn, _) = Wrap((string text) => text, "t", new ToolGovernance { MaxArgumentChars = 10 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => fn.InvokeAsync(new AIFunctionArguments { ["text"] = new string('y', 50) }, Ct).AsTask());
    }

    [Fact]
    public void Registry_IgnoresDuplicateNames_AndFindsByName()
    {
        var a = new ToolDescriptor(AIFunctionFactory.Create(() => "a", "dup"), Role.Users, ToolScope.All, "one");
        var b = new ToolDescriptor(AIFunctionFactory.Create(() => "b", "DUP"), Role.Users, ToolScope.All, "two");
        var registry = new ToolRegistry([new StaticSource([a]), new StaticSource([b])], NullLogger<ToolRegistry>.Instance);

        Assert.Single(registry.All());
        Assert.Equal("one", registry.Find("dup")!.Source);
        Assert.Single(registry.GetTools(CallerIdentity.Local("u", Role.Users), ToolScope.Coding, Core.Channels.Channel.Cli, ["dup"]));
        Assert.Empty(registry.GetTools(CallerIdentity.Local("u", Role.Users), ToolScope.Coding, Core.Channels.Channel.Cli, ["other"]));
    }

    private sealed class StaticSource(IEnumerable<ToolDescriptor> tools) : IToolSource
    {
        public IEnumerable<ToolDescriptor> GetTools() => tools;
    }
}
