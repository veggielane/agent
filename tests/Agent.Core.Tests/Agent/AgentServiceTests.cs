using Agent.Core.Agent;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Conversations;
using Agent.Core.Tests.Support;
using Agent.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Core.Tests.Agent;

public sealed class AgentServiceTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private sealed class EchoTools : IToolSource
    {
        public int Calls { get; private set; }

        public IEnumerable<ToolDescriptor> GetTools()
        {
            yield return new ToolDescriptor(
                AIFunctionFactory.Create((string text) => { Calls++; return "echo:" + text; }, "echo", "Echoes text"),
                Role.Users,
                ToolScope.Answer,
                "native");
            yield return new ToolDescriptor(
                AIFunctionFactory.Create(() => "secret", "team_only", "Team tool"),
                Role.Team,
                ToolScope.Answer,
                "native");
        }
    }

    private static AgentRequest Request(CallerIdentity caller, string text = "question", string? model = null, bool fromClient = false) => new()
    {
        Caller = caller,
        Channel = caller.Channel,
        ConversationId = "conv",
        Text = text,
        ModelOverride = model,
        ModelOverrideFromClient = fromClient,
    };

    [Fact]
    public async Task Answer_BuildsSystemPrompt_AndOffersOnlyPermittedTools()
    {
        var tools = new EchoTools();
        using var host = TestHost.Create(s => s.AddSingleton<IToolSource>(tools));
        host.Llm.Client.Reply("done");

        var response = await host.Get<IAgent>().AnswerAsync(Request(CallerIdentity.Local("alice", Role.Users)), Ct);

        Assert.Equal("done", response.Markdown);
        var call = host.Llm.Client.Calls.Single();
        Assert.Contains("alice", call.Messages[0].Text);
        Assert.Contains("Cli", call.Messages[0].Text);
        var offered = call.Options!.Tools!.OfType<AIFunction>().Select(t => t.Name).ToList();
        Assert.Contains("echo", offered);
        Assert.DoesNotContain("team_only", offered);
    }

    [Fact]
    public async Task Answer_ExecutesToolCalls_AndReportsToolsUsed()
    {
        var tools = new EchoTools();
        using var host = TestHost.Create(s => s.AddSingleton<IToolSource>(tools));
        host.Llm.Client
            .CallTool("echo", new Dictionary<string, object?> { ["text"] = "hi" })
            .Reply((messages, _) =>
            {
                var toolResult = messages.Last(m => m.Role == ChatRole.Tool).Contents.OfType<FunctionResultContent>().Single();
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "tool said " + toolResult.Result));
            });

        var response = await host.Get<IAgent>().AnswerAsync(Request(CallerIdentity.Local("alice", Role.Users)), Ct);

        Assert.Equal("tool said echo:hi", response.Markdown);
        Assert.Equal(["echo"], response.ToolsUsed);
        Assert.Equal(1, tools.Calls);
    }

    [Fact]
    public async Task Answer_EmptyText_GetsPlaceholder()
    {
        using var host = TestHost.Create();
        host.Llm.Client.Reply(string.Empty);
        var response = await host.Get<IAgent>().AnswerAsync(Request(CallerIdentity.Local("alice", Role.Users)), Ct);
        Assert.Contains("no text", response.Markdown);
    }

    [Fact]
    public async Task ModelOverride_FromClient_IgnoredUnlessAllowed()
    {
        using var host = TestHost.Create();
        host.Llm.Client.Reply("a").Reply("b");
        var agent = host.Get<IAgent>();

        await agent.AnswerAsync(Request(CallerIdentity.Local("alice", Role.Users), model: "big", fromClient: true), Ct);
        Assert.Null(host.Llm.Requests[0].ExplicitModel);

        await agent.AnswerAsync(Request(CallerIdentity.Local("alice", Role.Users), model: "big"), Ct);
        Assert.Equal("big", host.Llm.Requests[1].ExplicitModel);
    }

    [Fact]
    public async Task ModelOverride_FromClient_HonouredWhenAllowed()
    {
        using var host = TestHost.Create(extraConfig: new Dictionary<string, string?> { ["Llm:AllowClientModelOverride"] = "true" });
        host.Llm.Client.Reply("a");
        await host.Get<IAgent>().AnswerAsync(Request(CallerIdentity.Local("alice", Role.Users), model: "big", fromClient: true), Ct);
        Assert.Equal("big", host.Llm.Requests[0].ExplicitModel);
    }

    [Fact]
    public async Task ConversationModel_IsUsed_WhenNoOverride()
    {
        using var host = TestHost.Create();
        host.Get<IConversationSettings>().SetModel("conv", "conv-model");
        host.Llm.Client.Reply("a");
        await host.Get<IAgent>().AnswerAsync(Request(CallerIdentity.Local("alice", Role.Users)), Ct);
        Assert.Equal("conv-model", host.Llm.Requests[0].ExplicitModel);
        Assert.Equal("Cli.Answer", host.Llm.Requests[0].OverrideKey);
    }

    [Fact]
    public async Task Stream_YieldsText()
    {
        using var host = TestHost.Create();
        host.Llm.WithFunctionInvocation = false;
        host.Llm.Client.Reply("streamed");
        var chunks = new List<string>();
        await foreach (var chunk in host.Get<IAgent>().StreamAsync(Request(CallerIdentity.Local("alice", Role.Users)), Ct))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("streamed", string.Concat(chunks));
    }

    [Fact]
    public async Task SystemPromptFile_OverridesDefault()
    {
        using var host = TestHost.Create();
        var promptsDir = Path.Combine(host.WorkDir, "prompts");
        Directory.CreateDirectory(promptsDir);
        File.WriteAllText(Path.Combine(promptsDir, "system.md"), "CUSTOM PROMPT");
        File.WriteAllText(Path.Combine(promptsDir, "system.jira.md"), "JIRA PROMPT");
        host.Llm.Client.Reply("a").Reply("b");

        await host.Get<IAgent>().AnswerAsync(Request(CallerIdentity.Local("alice", Role.Users)), Ct);
        Assert.StartsWith("CUSTOM PROMPT", host.Llm.Client.Calls[0].Messages[0].Text);

        await host.Get<IAgent>().AnswerAsync(Request(new CallerIdentity(Channel.Jira, "alice") { Roles = new HashSet<Role> { Role.Users }, RolesResolved = true }), Ct);
        Assert.StartsWith("JIRA PROMPT", host.Llm.Client.Calls[1].Messages[0].Text);
    }
}
