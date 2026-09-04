using Agent.Host;
using Agent.Host.Api;

var bootstrap = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var apiEnabled = bootstrap.GetValue("Api:Enabled", true);

if (apiEnabled)
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
    builder.Services.AddWindowsService(o => o.ServiceName = "TeamAgent");
    builder.WebHost.UseUrls(builder.Configuration.GetValue("Api:ListenUrl", "http://localhost:5080")!);

    builder.Services.AddAgent(builder.Configuration);
    builder.Services.AddAgentApi(builder.Configuration);

    var app = builder.Build();
    app.MapAgentApi();
    app.Logger.LogInformation("Team agent starting with API on {Url}", builder.Configuration["Api:ListenUrl"] ?? "http://localhost:5080");
    app.Run();
}
else
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
    builder.Services.AddWindowsService(o => o.ServiceName = "TeamAgent");
    builder.Services.AddAgent(builder.Configuration);

    var host = builder.Build();
    host.Services.GetRequiredService<ILogger<Program>>().LogInformation("Team agent starting as a worker (API disabled)");
    host.Run();
}

/// <summary>Exposed for WebApplicationFactory in tests.</summary>
public partial class Program
{
}
