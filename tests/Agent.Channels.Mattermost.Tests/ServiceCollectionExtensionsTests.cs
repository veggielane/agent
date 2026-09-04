using Agent.Core.Conversations;
using Agent.Core.Events;
using Agent.Core.Replies;
using Agent.Core.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Mattermost.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IInboundQueue>(new InboundQueue());
        services.AddSingleton<ICursorStore, InMemoryCursorStore>();
        services.AddMattermostChannel(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static Dictionary<string, string?> ValidSettings() => new(StringComparer.Ordinal)
    {
        ["Mattermost:BaseUrl"] = "https://chat.example.test",
        ["Mattermost:BotToken"] = "secret-token",
        ["Mattermost:ThreadFollowMinutes"] = "15",
        ["Mattermost:ChannelAllowList:0"] = "dev-team",
    };

    [Fact]
    public void AddMattermostChannel_RegistersListenerAsHostedServiceAndEventSource()
    {
        using var provider = Build(ValidSettings());

        var hosted = Assert.Single(provider.GetServices<IHostedService>());
        var source = Assert.Single(provider.GetServices<IEventSource>());

        Assert.IsType<MattermostListener>(hosted);
        Assert.Same(hosted, source);
        Assert.Same(hosted, provider.GetRequiredService<MattermostListener>());
    }

    [Fact]
    public void AddMattermostChannel_RegistersChannelServices()
    {
        using var provider = Build(ValidSettings());

        Assert.IsType<MattermostReplySender>(Assert.Single(provider.GetServices<IReplySender>()));
        Assert.IsType<MattermostConversationContextProvider>(Assert.Single(provider.GetServices<IConversationContextProvider>()));
        Assert.IsType<MattermostTaskNotifier>(Assert.Single(provider.GetServices<ITaskNotifier>()));
        Assert.IsType<ClientWebSocketMattermostSocketFactory>(provider.GetRequiredService<IMattermostSocketFactory>());
        Assert.IsType<MattermostUserDirectory>(provider.GetRequiredService<IMattermostUserDirectory>());
        Assert.NotNull(provider.GetRequiredService<MattermostEventMapper>());
    }

    [Fact]
    public void AddMattermostChannel_BindsOptions()
    {
        using var provider = Build(ValidSettings());

        var options = provider.GetRequiredService<IOptionsMonitor<MattermostOptions>>().CurrentValue;

        Assert.Equal("https://chat.example.test", options.BaseUrl);
        Assert.Equal(15, options.ThreadFollowMinutes);
        Assert.Equal(["dev-team"], options.ChannelAllowList);
        Assert.Equal(16383, options.MaxPostLength);
    }

    [Fact]
    public void AddMattermostChannel_ConfiguresTypedClientWithBaseAddress()
    {
        using var provider = Build(ValidSettings());

        var client = provider.GetRequiredService<IMattermostClient>();

        Assert.IsType<MattermostClient>(client);
    }

    [Fact]
    public void AddMattermostChannel_MissingBotToken_FailsValidation()
    {
        var settings = ValidSettings();
        settings.Remove("Mattermost:BotToken");
        using var provider = Build(settings);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<MattermostOptions>>().Value);

        Assert.Contains("BotToken", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddMattermostChannel_InvalidBaseUrl_FailsValidation()
    {
        var settings = ValidSettings();
        settings["Mattermost:BaseUrl"] = "chat.example.test";
        using var provider = Build(settings);

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<MattermostOptions>>().Value);
    }
}
