using Agent.Coding.Sandbox;
using Agent.Core;
using Agent.Core.Infrastructure;
using Agent.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agent.Coding.Tests;

public sealed class ServiceRegistrationTests
{
    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void AddAgentWorker_ResolvesRunnerWorkerAndEngine()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["Llm:BaseUrl"] = "http://llm.invalid/v1",
            ["Llm:AnswerModel"] = "m",
            ["Coding:WorkspaceRoot"] = Path.GetTempPath(),
        });
        var services = new ServiceCollection().AddAgentCore(config).AddAgentWorker(config);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<TaskRunner>(provider.GetRequiredService<ITaskRunner>());
        Assert.IsType<CodingEngine>(provider.GetRequiredService<ICodingEngine>());
        Assert.IsType<WorkspaceManager>(provider.GetRequiredService<IWorkspaceManager>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is TaskWorker);
        Assert.Contains(provider.GetServices<IStatusContributor>(), s => s is TaskWorker);
        Assert.Same(provider.GetRequiredService<TaskWorker>(), provider.GetServices<IHostedService>().OfType<TaskWorker>().Single());
    }

    [Fact]
    public void AddAgentCoding_Defaults_WhenSectionMissing()
    {
        using var provider = new ServiceCollection().AddAgentCoding(Config([])).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<CodingOptions>>().CurrentValue;

        Assert.Equal(2, options.MaxConcurrentTasks);
        Assert.Equal(60, options.Budget.MaxTurns);
        Assert.Equal(CodingOptions.DefaultAllowedExecutables, options.AllowedExecutables);
        Assert.Equal(CodingOptions.DefaultProtectedPaths, options.ProtectedPaths);
        Assert.Equal("agent/", options.BranchPrefix);
    }

    [Fact]
    public void AddAgentCoding_ConfiguredLists_ReplaceDefaults()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["Coding:AllowedExecutables:0"] = "dotnet",
            ["Coding:AllowedExecutables:1"] = "git",
            ["Coding:ProtectedPaths:0"] = "infra/**",
            ["Coding:Budget:MaxTurns"] = "5",
            ["Coding:OpenAsDraft"] = "true",
        });
        using var provider = new ServiceCollection().AddAgentCoding(config).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<CodingOptions>>().CurrentValue;

        Assert.Equal(["dotnet", "git"], options.AllowedExecutables);
        Assert.Equal(["infra/**"], options.ProtectedPaths);
        Assert.Equal(5, options.Budget.MaxTurns);
        Assert.True(options.OpenAsDraft);
        Assert.Equal(CodingOptions.DefaultAllowedGitSubcommands, options.AllowedGitSubcommands);
    }

    [Fact]
    public void AddAgentCoding_SandboxDefaultsToProcess_AndIsAStatusContributor()
    {
        using var provider = new ServiceCollection().AddAgentCoding(Config([])).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<CodingOptions>>().CurrentValue;

        Assert.Equal(SandboxMode.Process, options.Sandbox.Mode);
        Assert.Equal("process", provider.GetRequiredService<ISandbox>().Name);
        Assert.IsType<SandboxSelector>(provider.GetRequiredService<ISandbox>());
        Assert.Contains(provider.GetServices<IStatusContributor>(), s => s is DockerSandbox);
    }

    [Fact]
    public void AddAgentCoding_DockerMode_IsSelected_AndListsReplaceDefaults()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["Coding:Sandbox:Mode"] = "Docker",
            ["Coding:Sandbox:Network"] = "none",
            ["Coding:Sandbox:DefaultImage"] = "our-registry/build:1",
            ["Coding:Sandbox:Images:node"] = "our-registry/node:22",
            ["Coding:Sandbox:Volumes:0"] = "agent-nuget:/root/.nuget/packages",
            ["Coding:Sandbox:ExtraArgs:0"] = "--dns",
            ["Coding:Sandbox:ExtraArgs:1"] = "10.0.0.1",
            ["Coding:Sandbox:ExtraArgs:2"] = "--dns",
            ["Coding:Sandbox:ExtraArgs:3"] = "10.0.0.2",
        });
        using var provider = new ServiceCollection().AddAgentCoding(config).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<CodingOptions>>().CurrentValue;

        Assert.Equal(SandboxMode.Docker, options.Sandbox.Mode);
        Assert.Equal("none", options.Sandbox.Network);
        Assert.Equal("our-registry/node:22", options.Sandbox.Images["node"]);
        Assert.Equal(["agent-nuget:/root/.nuget/packages"], options.Sandbox.Volumes);
        // Repeated flags survive: de-duplicating raw docker arguments would corrupt them.
        Assert.Equal(["--dns", "10.0.0.1", "--dns", "10.0.0.2"], options.Sandbox.ExtraArgs);
        Assert.Equal("docker", provider.GetRequiredService<ISandbox>().Name);
    }
}
