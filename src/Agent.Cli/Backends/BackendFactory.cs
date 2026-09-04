using Agent.Cli.Auth;
using Agent.Cli.Commands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Agent.Cli.Backends;

public sealed class BackendFactory
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClients;
    private readonly IAccessTokenProvider _tokens;
    private readonly ILoggerFactory _loggerFactory;

    public BackendFactory(IConfiguration configuration, IHttpClientFactory httpClients, IAccessTokenProvider tokens, ILoggerFactory loggerFactory)
    {
        _configuration = configuration;
        _httpClients = httpClients;
        _tokens = tokens;
        _loggerFactory = loggerFactory;
    }

    public IAgentBackend Create(GlobalSettings settings)
    {
        var server = settings.Server ?? _configuration["Agent:Server"] ?? Environment.GetEnvironmentVariable("AGENT_SERVER");
        var local = settings.Local || string.Equals(_configuration["Agent:Mode"], "local", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(server);

        if (local)
        {
            return new LocalBackend(_configuration, _loggerFactory);
        }

        var http = _httpClients.CreateClient("agent-api");
        http.BaseAddress = new Uri(server!.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromMinutes(10);
        return new RemoteBackend(http, _tokens);
    }
}
