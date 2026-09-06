using Agent.Channels.GitLab;
using Agent.Core;
using Agent.Core.Authorization;
using Agent.Core.Commands;
using Agent.Core.Conversations;
using Agent.Core.Events;
using Agent.Core.Replies;
using Agent.Core.Tasks;
using Agent.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agent.Channels.GitLab.Tests;

public sealed class ServiceCollectionExtensionsTests : IDisposable
{
    private readonly GitLabTestServer _gitlab = new();

    public void Dispose() => _gitlab.Dispose();

    private IConfiguration Configuration(string? baseUrl = null, string token = "glpat-from-config") => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:BaseUrl"] = "https://llm.test/v1",
            ["Llm:AnswerModel"] = "test-model",
            ["GitLab:BaseUrl"] = baseUrl ?? _gitlab.Url,
            ["GitLab:Token"] = token,
            ["GitLab:BotUsername"] = "agent-bot",
            ["GitLab:Groups:0"] = "team",
            ["GitLab:MrLabels:0"] = "bot",
        })
        .Build();

    [Fact]
    public async Task AddGitLabClient_TypedClientSendsPrivateTokenFromConfiguration()
    {
        _gitlab.Get("/api/v4/user", Payloads.User(7, "agent-bot"));
        var services = new ServiceCollection().AddLogging().AddGitLabClient(Configuration());
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IGitLabClient>();
        var me = await client.GetCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.Equal("agent-bot", me.Username);
        var request = Assert.Single(_gitlab.Requests("GET", "/api/v4/user"));
        Assert.Equal("glpat-from-config", request.Headers!["PRIVATE-TOKEN"].Single());
    }

    [Fact]
    public void AddGitLabClient_RegistersToolsPublisherAndCredentials()
    {
        var services = new ServiceCollection().AddLogging().AddGitLabClient(Configuration());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<GitLabTools>(Assert.Single(provider.GetServices<IToolSource>()));
        Assert.IsType<GitLabMergeRequestPublisher>(provider.GetRequiredService<IMergeRequestPublisher>());
        Assert.IsType<GitLabCredentialProvider>(provider.GetRequiredService<IRepositoryCredentialProvider>());
        var options = provider.GetRequiredService<IOptionsMonitor<GitLabOptions>>().CurrentValue;
        Assert.Equal(["team"], options.Groups);
        Assert.Equal(["bot"], options.EffectiveMrLabels);
        Assert.Equal(30, options.PollSeconds);
    }

    [Fact]
    public void AddGitLabChannel_RegistersPollerOnceAsHostedServiceAndEventSource()
    {
        var configuration = Configuration();
        var services = new ServiceCollection().AddAgentCore(configuration).AddGitLabChannel(configuration);
        using var provider = services.BuildServiceProvider();

        var hosted = Assert.Single(provider.GetServices<IHostedService>().OfType<GitLabPoller>());
        var source = Assert.Single(provider.GetServices<IEventSource>().OfType<GitLabPoller>());
        Assert.Same(hosted, source);
        Assert.Contains(provider.GetServices<IReplySender>(), s => s is GitLabReplySender);
        Assert.Contains(provider.GetServices<IConversationContextProvider>(), p => p is GitLabConversationContextProvider);
        Assert.Contains(provider.GetServices<ITaskNotifier>(), n => n is GitLabTaskNotifier);
        Assert.Contains(provider.GetServices<IToolSource>(), t => t is GitLabTools);
    }

    [Fact]
    public void AddGitLabChannel_CalledAfterAddGitLabClient_RegistersEachServiceOnce()
    {
        var configuration = Configuration();
        var services = new ServiceCollection().AddAgentCore(configuration).AddGitLabClient(configuration).AddGitLabChannel(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IToolSource>().OfType<GitLabTools>());
        Assert.Single(provider.GetServices<IHostedService>().OfType<GitLabPoller>());
        Assert.Single(provider.GetServices<IEventSource>().OfType<GitLabPoller>());
        Assert.Single(provider.GetServices<IReplySender>().OfType<GitLabReplySender>());
        Assert.Single(provider.GetServices<ITaskNotifier>().OfType<GitLabTaskNotifier>());
    }

    [Fact]
    public void AddGitLabClient_RegistersTheFixCommand()
    {
        var configuration = Configuration();
        var services = new ServiceCollection().AddAgentCore(configuration).AddGitLabClient(configuration);
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ICommandRegistry>();

        Assert.True(registry.TryGet("fix", out var command));
        Assert.True(registry.TryGet("implement", out var alias));
        Assert.Same(command, alias);
        Assert.Equal(Role.Team, command.Role);
        Assert.Empty(registry.Problems);
    }

    [Fact]
    public void AddGitLabClient_MissingToken_FailsValidation()
    {
        var services = new ServiceCollection().AddLogging().AddGitLabClient(Configuration(token: ""));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GitLabOptions>>().Value);
    }

    [Fact]
    public void AddGitLabClient_RelativeBaseUrl_FailsValidation()
    {
        var services = new ServiceCollection().AddLogging().AddGitLabClient(Configuration(baseUrl: "gitlab.internal"));
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GitLabOptions>>().Value);
        Assert.Contains("BaseUrl", ex.Message, StringComparison.Ordinal);
    }
}
