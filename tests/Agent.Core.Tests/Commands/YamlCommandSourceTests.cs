using Agent.Core.Channels;
using Agent.Core.Commands;
using Agent.Core.Commands.Yaml;
using Agent.Core.Events;
using Agent.Core.Tasks;
using Agent.Core.Tests.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Core.Tests.Commands;

public sealed class YamlCommandSourceTests
{
    private const string Summarize =
        """
        name: summarize
        aliases: [tldr]
        description: Summarise the thread
        role: users
        model: AnswerModel
        tools: [none]
        prompt: |
          Summarise in 3 bullets. Extra: {{args}}
          Caller: {{caller}}
          ---
          {{context}}
        """;

    private const string Fix =
        """
        name: fix
        description: Start a task from this issue
        role: team
        kind: task
        channels: [gitlab, jira]
        prompt: |
          Fix {{issue}} in {{repo}}: {{args}}
        """;

    [Fact]
    public async Task AskCommand_RendersPlaceholders_AndCallsAgent()
    {
        using var host = TestHost.Create();
        File.WriteAllText(Path.Combine(host.CommandsDir, "summarize.yaml"), Summarize);
        host.Get<ICommandRegistry>().Reload();
        host.Context.History.Add(new ChatMessage(ChatRole.User, "first message") { AuthorName = "carol" });
        host.Llm.Client.Reply("- one\n- two\n- three");

        var caller = await host.ResolveAsync("alice");
        var evt = host.Event("!tldr focus on risks", "alice");
        var result = await host.Get<ICommandDispatcher>().DispatchAsync(evt.Text, host.CommandContext(caller, evt: evt));

        Assert.Equal("- one\n- two\n- three", result.Markdown);
        var prompt = host.Llm.Client.Calls.Single().Messages.Last().Text;
        Assert.Contains("Extra: focus on risks", prompt);
        Assert.Contains("Caller: alice", prompt);
        Assert.Contains("carol: first message", prompt);
        Assert.Null(host.Llm.Client.Calls.Single().Options?.Tools);
        Assert.Equal("test-model", host.Llm.Requests.Single().ExplicitModel);
    }

    [Fact]
    public async Task TaskCommand_CreatesTask_FromEventContext()
    {
        using var host = TestHost.Create(channel: Channel.GitLab);
        File.WriteAllText(Path.Combine(host.CommandsDir, "fix.yaml"), Fix);
        host.Get<ICommandRegistry>().Reload();

        var caller = await host.ResolveAsync("bob", Channel.GitLab);
        var evt = host.Event("!fix add null checks", "bob", task: new TaskContext
        {
            Source = TaskSource.GitLabIssue,
            SourceRef = "team/repo#7",
            RepoUrl = "https://gitlab.internal/team/repo.git",
            ProjectId = "42",
            BaseBranch = "main",
        });

        var result = await host.Get<ICommandDispatcher>().DispatchAsync(evt.Text, host.CommandContext(caller, evt: evt));

        Assert.Contains("task #1", result.Markdown);
        var task = await host.Get<ITaskService>().GetAsync(1);
        Assert.NotNull(task);
        Assert.Equal(AgentTaskStatus.Queued, task.Status);
        Assert.Equal("Fix team/repo#7 in https://gitlab.internal/team/repo.git: add null checks", task.Instruction);
    }

    [Fact]
    public async Task TaskCommand_WithoutContext_Errors()
    {
        using var host = TestHost.Create(channel: Channel.GitLab);
        File.WriteAllText(Path.Combine(host.CommandsDir, "fix.yaml"), Fix);
        host.Get<ICommandRegistry>().Reload();

        var caller = await host.ResolveAsync("bob", Channel.GitLab);
        var result = await host.Get<ICommandDispatcher>().DispatchAsync("!fix x", host.CommandContext(caller, evt: host.Event("!fix x", "bob")));
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task TaskCommand_RestrictedChannels_Enforced()
    {
        using var host = TestHost.Create();
        File.WriteAllText(Path.Combine(host.CommandsDir, "fix.yaml"), Fix);
        host.Get<ICommandRegistry>().Reload();

        var caller = await host.ResolveAsync("bob");
        var result = await host.Get<ICommandDispatcher>().DispatchAsync("!fix x", host.CommandContext(caller));
        Assert.Contains("not available on Mattermost", result.Markdown);
    }

    [Theory]
    [InlineData("name: a\nprompt: hi\n", "role")]
    [InlineData("name: a\nrole: users\n", "prompt")]
    [InlineData("name: a\nrole: users\nprompt: '{{nope}}'\n", "placeholder")]
    [InlineData("name: a\nrole: users\nkind: weird\nprompt: hi\n", "kind")]
    [InlineData("name: a\nrole: users\nchannels: [teams]\nprompt: hi\n", "channel")]
    [InlineData("role: users\nprompt: hi\n", "name")]
    public void InvalidDefinitions_AreReportedAsProblems(string yaml, string expectedWord)
    {
        using var host = TestHost.Create();
        File.WriteAllText(Path.Combine(host.CommandsDir, "bad.yaml"), yaml);
        var source = host.Get<YamlCommandSource>();

        var commands = source.GetCommands().ToList();

        Assert.Empty(commands);
        Assert.Single(source.Problems);
        Assert.Contains(expectedWord, source.Problems[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_SetsMetadata()
    {
        using var host = TestHost.Create();
        var def = new PromptCommandDefinition { Name = "X", Role = "Admin", Prompt = "p", Aliases = ["y"], Hidden = true, Description = "d" };
        var cmd = host.Get<YamlCommandSource>().Build(def, "x.yaml");

        Assert.Equal("x", cmd.Name);
        Assert.Equal(Core.Authorization.Role.Admin, cmd.Role);
        Assert.Equal(["y"], cmd.Aliases);
        Assert.True(cmd.Hidden);
        Assert.Equal("x.yaml", cmd.Source);
    }
}
