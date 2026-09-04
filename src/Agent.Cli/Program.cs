using Agent.Cli.Auth;
using Agent.Cli.Backends;
using Agent.Cli.Commands;
using Agent.Cli.Infrastructure;
using Agent.Infrastructure.Keycloak;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent", "appsettings.json"), optional: true)
    .AddUserSecrets("agent-cli")
    .AddEnvironmentVariables("AGENT_")
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.AddLogging(b =>
{
    b.AddConfiguration(configuration.GetSection("Logging"));
    b.AddSimpleConsole(o => o.SingleLine = true);
    b.SetMinimumLevel(LogLevel.Warning);
});
services.AddHttpClient("agent-api");
services.AddKeycloakCli(configuration);
services.AddSingleton<IAccessTokenProvider, KeycloakAccessTokenProvider>();
services.AddSingleton<BackendFactory>();

var app = new CommandApp(new DependencyInjectionRegistrar(services));
app.Configure(config =>
{
    config.SetApplicationName("agent");
    config.AddCommand<AskCommand>("ask").WithDescription("Ask one question (or run a !command) and print the answer.").WithExample("ask", "\"what is open on PROJ-12?\"");
    config.AddCommand<ChatCommand>("chat").WithDescription("Interactive chat with streaming replies.");
    config.AddCommand<CmdCommand>("cmd").WithDescription("Run a !command, e.g. `agent cmd status`.").WithExample("cmd", "tasks", "--all");
    config.AddBranch("task", task =>
    {
        task.SetDescription("Coding tasks.");
        task.AddCommand<TaskCreateCommand>("create").WithDescription("Create a coding task for a repository.").WithExample("task", "create", "--repo", "https://gitlab.internal/team/repo.git", "\"add retry to the http client\"");
        task.AddCommand<TaskListCommand>("list").WithDescription("List tasks.");
        task.AddCommand<TaskShowCommand>("show").WithDescription("Show one task.");
        task.AddCommand<TaskLogCommand>("log").WithDescription("Show a task's events.");
        task.AddCommand<TaskCancelCommand>("cancel").WithDescription("Cancel a task.");
    });
    config.AddCommand<LoginCommand>("login").WithDescription("Sign in to Keycloak (device flow).");
    config.AddCommand<LogoutCommand>("logout").WithDescription("Forget cached tokens.");
    config.AddCommand<WhoAmICommand>("whoami").WithDescription("Show how the agent sees you.");
    config.PropagateExceptions();
});

try
{
    return await app.RunAsync(args);
}
catch (Exception ex)
{
    Spectre.Console.AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
    return 1;
}
