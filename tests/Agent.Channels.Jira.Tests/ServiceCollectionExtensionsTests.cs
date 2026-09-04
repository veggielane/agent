using Agent.Channels.Jira;
using Agent.Core.Conversations;
using Agent.Core.Events;
using Agent.Core.Formatting;
using Agent.Core.Replies;
using Agent.Core.Tasks;
using Agent.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WireMock.Server;

namespace Agent.Channels.Jira.Tests;

public sealed class ServiceCollectionExtensionsTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _server.Dispose();

    private IConfiguration Configuration(params (string Key, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["Jira:BaseUrl"] = _server.Url,
            ["Jira:Username"] = "agent-bot",
            ["Jira:Token"] = "secret-token",
            ["Jira:Projects:0"] = "PROJ",
            ["Jira:ProjectRepos:PROJ"] = "https://gitlab.corp.local/team/repo",
            ["Jira:ComponentRepos:api"] = "https://gitlab.corp.local/team/api",
            ["Jira:PollSeconds"] = "1",
            ["Jira:TimeZone"] = "UTC",
        };

        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IServiceCollection CoreStubs()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IInboundQueue>(new InboundQueue());
        services.AddSingleton<ICursorStore, InMemoryCursorStore>();
        services.AddSingleton<IProcessedEventStore, InMemoryProcessedEventStore>();
        services.AddSingleton<ITaskStore, InMemoryTaskStore>();
        services.AddSingleton<IFormatterRegistry, FormatterRegistry>();
        return services;
    }

    [Fact]
    public async Task AddJiraChannel_RegistersEverythingWithASinglePollerInstance()
    {
        var services = CoreStubs().AddJiraChannel(Configuration());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var options = provider.GetRequiredService<IOptionsMonitor<JiraOptions>>().CurrentValue;
        Assert.Equal("agent-bot", options.Username);
        Assert.Equal(["PROJ"], options.Projects);
        Assert.Equal("https://gitlab.corp.local/team/repo", options.ProjectRepos["proj"]);
        Assert.Equal("https://gitlab.corp.local/team/api", options.ComponentRepos["API"]);

        var eventSource = Assert.Single(provider.GetServices<IEventSource>());
        var hosted = Assert.Single(provider.GetServices<IHostedService>());
        Assert.Same(eventSource, hosted);
        Assert.IsType<JiraPoller>(eventSource);

        Assert.IsType<JiraReplySender>(Assert.Single(provider.GetServices<IReplySender>()));
        Assert.IsType<JiraConversationContextProvider>(Assert.Single(provider.GetServices<IConversationContextProvider>()));
        Assert.IsType<JiraTaskNotifier>(Assert.Single(provider.GetServices<ITaskNotifier>()));
        Assert.IsType<JiraTools>(Assert.Single(provider.GetServices<IToolSource>()));
        Assert.IsType<JiraWikiFormatter>(Assert.Single(provider.GetServices<IResponseFormatter>()));
        Assert.IsType<JiraRepositoryResolver>(provider.GetRequiredService<IRepositoryResolver>());
        Assert.Equal("*bold*", provider.GetRequiredService<IFormatterRegistry>().Format(Agent.Core.Channels.Channel.Jira, "**bold**"));
    }

    [Fact]
    public async Task AddJiraClient_ThenAddJiraChannel_DoesNotDuplicateSharedServices()
    {
        var configuration = Configuration();
        var services = CoreStubs().AddJiraClient(configuration).AddJiraChannel(configuration);
        await using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IToolSource>());
        Assert.Single(provider.GetServices<IResponseFormatter>());
        Assert.Single(provider.GetServices<IReplySender>());
    }

    [Fact]
    public async Task AddJiraClient_TypedClientTalksToJiraThroughTheResiliencePipeline()
    {
        Stubs.Myself(_server);
        await using var provider = CoreStubs().AddJiraClient(Configuration()).BuildServiceProvider();

        var me = await provider.GetRequiredService<IJiraClient>().GetMyselfAsync(_ct);

        Assert.Equal("agent-bot", me.Name);
        var request = Assert.Single(_server.Requests());
        Assert.Equal("Bearer secret-token", request.Header("Authorization"));
    }

    [Fact]
    public async Task AddJiraClient_RetriesTransientFailuresThroughTheStandardHandler()
    {
        _server.Given(WireMock.RequestBuilders.Request.Create().WithPath("/rest/api/2/myself").UsingGet())
            .InScenario("flaky").WillSetStateTo("recovered")
            .RespondWith(Stubs.Error(503, "warming up"));
        _server.Given(WireMock.RequestBuilders.Request.Create().WithPath("/rest/api/2/myself").UsingGet())
            .InScenario("flaky").WhenStateIs("recovered")
            .RespondWith(Stubs.Json(new { name = "agent-bot" }));
        await using var provider = CoreStubs().AddJiraClient(Configuration()).BuildServiceProvider();

        var me = await provider.GetRequiredService<IJiraClient>().GetMyselfAsync(_ct);

        Assert.Equal("agent-bot", me.Name);
        Assert.Equal(2, _server.LogEntries.Count());
    }

    [Fact]
    public async Task AddJiraClient_MissingBaseUrlOrUsername_FailsValidation()
    {
        await using var provider = CoreStubs().AddJiraClient(Configuration(("Jira:BaseUrl", null), ("Jira:Username", ""))).BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<JiraOptions>>().Value);

        Assert.Contains("BaseUrl", ex.Message);
        Assert.Contains("Username", ex.Message);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }
}
