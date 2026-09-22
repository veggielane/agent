using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Agent.Core.Llm;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agent.Host.Tests.Support;

public sealed class AgentApiFactory : WebApplicationFactory<Program>
{
    public const string Authority = "https://sso.test/realms/test";
    public const string Audience = "agent-api";
    public const string SigningKey = "unit-test-signing-key-0123456789-abcdefghijklmnop";

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "agent-host-tests", Guid.NewGuid().ToString("N"));

    public FakeChatClientFactory Llm { get; } = new();

    /// <summary>Set before the first client is created to replace the repository policy the host registers.</summary>
    public Agent.Core.Tasks.IRepositoryPolicy? RepositoryPolicy { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_workDir);
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:Enabled"] = "true",
                ["Api:DevSigningKey"] = SigningKey,
                ["Keycloak:Authority"] = Authority,
                ["Keycloak:ApiAudience"] = Audience,
                ["Keycloak:CliClientId"] = "agent-cli",
                ["Keycloak:Admin:ClientId"] = "svc",
                ["Keycloak:Admin:ClientSecret"] = "secret",
                ["Llm:BaseUrl"] = "http://localhost:1/v1",
                ["Llm:AnswerModel"] = "test-model",
                ["Authorization:Provider"] = "Static",
                ["Authorization:Roles:Users:0"] = "/agent/users",
                ["Authorization:Roles:Team:0"] = "/agent/team",
                ["Authorization:Roles:Admin:0"] = "/agent/admin",
                ["Authorization:CacheMinutes"] = "0",
                ["Persistence:ConnectionString"] = string.Empty,
                ["Mattermost:BaseUrl"] = string.Empty,
                ["Jira:BaseUrl"] = string.Empty,
                ["GitLab:BaseUrl"] = string.Empty,
                ["Ldap:Enabled"] = "false",
                ["Commands:Directory"] = Path.Combine(_workDir, "commands"),
                ["Prompts:Directory"] = Path.Combine(_workDir, "prompts"),
                ["Mcp:Directory"] = Path.Combine(_workDir, "mcp"),
                ["Coding:WorkspaceRoot"] = Path.Combine(_workDir, "work"),
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IChatClientFactory>(Llm));
            if (RepositoryPolicy is not null)
            {
                services.Replace(ServiceDescriptor.Singleton(RepositoryPolicy));
            }

            // These tests exercise the HTTP surface, not the background pipeline. Leaving the workers in
            // means the task worker races each test, claiming queued tasks and trying to clone them.
            services.RemoveAll<IHostedService>();
        });
    }

    public HttpClient CreateClientFor(string username, params string[] groups)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(username, groups));
        return client;
    }

    public static string CreateToken(string username, string[] groups, string? issuer = Authority, string? audience = Audience)
    {
        var handler = new JsonWebTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddHours(1),
            IssuedAt = DateTime.UtcNow,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = $"id-{username}",
                ["preferred_username"] = username,
                ["email"] = $"{username}@test.local",
                ["groups"] = groups,
            },
        };

        return handler.CreateToken(descriptor);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class FakeChatClientFactory : IChatClientFactory
{
    public ScriptedChatClient Client { get; } = new();

    public List<(ModelPurpose Purpose, string? OverrideKey, string? ExplicitModel)> Requests { get; } = [];

    public IChatClient Create(ModelPurpose purpose, string? overrideKey = null, string? explicitModel = null)
    {
        Requests.Add((purpose, overrideKey, explicitModel));
        return new ChatClientBuilder(Client).UseFunctionInvocation().Build();
    }

    public string ResolveModel(ModelPurpose purpose, string? overrideKey = null, string? explicitModel = null) => explicitModel ?? "test-model";
}

public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<string> _replies = new();

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public ScriptedChatClient Reply(string text)
    {
        _replies.Enqueue(text);
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(messages.ToList());
        var text = _replies.Count > 0 ? _replies.Dequeue() : "default reply";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var word in response.Text.Split(' '))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
