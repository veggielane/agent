using Agent.Core.Authorization;
using Agent.Core.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Agent.Coding.Tests;

public sealed class TaskPlannerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly CallerIdentity _requester = CallerIdentity.Local("alice", Role.Team);

    public TaskPlannerTests()
    {
        Directory.CreateDirectory(_dir.Combine("repo", "src"));
        File.WriteAllText(_dir.Combine("repo", "README.md"), "# Repo");
    }

    public void Dispose() => _dir.Dispose();

    private (TaskPlanner Planner, ScriptedChatClient Script, Workspace Workspace) Build(Action<CodingOptions>? configure = null, RepoProfile? profile = null)
    {
        var script = new ScriptedChatClient();
        var factory = Substitute.For<IChatClientFactory>();
        factory.Create(ModelPurpose.Answer, Arg.Any<string?>(), Arg.Any<string?>()).Returns(script);
        factory.ResolveModel(ModelPurpose.Answer, Arg.Any<string?>(), Arg.Any<string?>()).Returns("answer-model");

        var options = TestOptions.Coding(_dir.Path, configure);
        var planner = new TaskPlanner(factory, TestOptions.Monitor(options), NullLogger<TaskPlanner>.Instance);
        var workspace = new Workspace(4, _dir.Path, _dir.Combine("repo"), "agent/x", "main", profile ?? RepoProfile.Empty);
        return (planner, script, workspace);
    }

    [Fact]
    public async Task PlanAsync_ReturnsThePlanAndShowsTheModelTheRepository()
    {
        var (planner, script, workspace) = Build(profile: new RepoProfile("dotnet build", "dotnet test", null, [], [], "Use file-scoped namespaces."));
        script.ThenText("**Understanding**\nAdd a greeting.\n\n**Plan**\n- add the file");

        var plan = await planner.PlanAsync(workspace, "Add a greeting file.", _requester, TestContext.Current.CancellationToken);

        Assert.StartsWith("**Understanding**", plan, StringComparison.Ordinal);

        var prompt = script.Calls.Single()[1].Text;
        Assert.Contains("Add a greeting file.", prompt, StringComparison.Ordinal);
        Assert.Contains("alice", prompt, StringComparison.Ordinal);
        Assert.Contains("agent/x", prompt, StringComparison.Ordinal);
        Assert.Contains("README.md", prompt, StringComparison.Ordinal);
        Assert.Contains("dotnet test", prompt, StringComparison.Ordinal);
        Assert.Contains("file-scoped", prompt, StringComparison.Ordinal);

        // Repository content is data, and the system prompt has to say so.
        Assert.Contains("data, not", script.Calls.Single()[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanAsync_Disabled_DoesNotCallTheModel()
    {
        var (planner, script, workspace) = Build(o => o.PostPlan = false);

        Assert.Null(await planner.PlanAsync(workspace, "Add a thing.", _requester, TestContext.Current.CancellationToken));
        Assert.Empty(script.Calls);
    }

    [Fact]
    public async Task PlanAsync_ModelFails_ReturnsNullRatherThanFailingTheTask()
    {
        var (planner, script, workspace) = Build();
        script.Then((_, _) => throw new HttpRequestException("llm down"));

        Assert.Null(await planner.PlanAsync(workspace, "Add a thing.", _requester, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlanAsync_EmptyResponse_IsTreatedAsNoPlan()
    {
        var (planner, script, workspace) = Build();
        script.ThenText("   ");

        Assert.Null(await planner.PlanAsync(workspace, "Add a thing.", _requester, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlanAsync_UsesTheAnswerModelWithABoundedOutput()
    {
        var (planner, script, workspace) = Build(o => o.PlanMaxTokens = 300);
        script.ThenText("plan");

        await planner.PlanAsync(workspace, "Add a thing.", _requester, TestContext.Current.CancellationToken);

        // A plan is a paragraph; it must not be billed like a coding run.
        Assert.Equal(300, script.LastOptions!.MaxOutputTokens);
    }
}
