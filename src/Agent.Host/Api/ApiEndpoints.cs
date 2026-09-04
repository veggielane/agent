using System.Text;
using System.Text.Json;
using Agent.Core.Agent;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Commands;
using Agent.Core.Events;
using Agent.Core.Pipeline;
using Agent.Core.Tasks;
using Agent.Infrastructure.Keycloak;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Agent.Host.Api;

public sealed record ChatRequest(string Text, string? ConversationId = null, string? Model = null, bool Stream = false);

public sealed record ChatResponseDto(string Markdown, string ConversationId, string? Model, IReadOnlyList<string> ToolsUsed, bool IsCommand, bool IsError);

public sealed record CreateTaskRequest(string RepoUrl, string Instruction, string? Title = null, string? BaseBranch = null, string? ProjectId = null);

public sealed record TaskDto(int Id, string Status, string Source, string SourceRef, string? Title, string? RepoUrl, string? WorkBranch, string? MergeRequestUrl, string? Summary, string? Error, int Turns, long TokensUsed, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string RequesterName)
{
    public static TaskDto From(AgentTask t) => new(t.Id, t.Status.ToString(), t.Source.ToString(), t.SourceRef, t.Title, t.RepoUrl, t.WorkBranch, t.MergeRequestUrl, t.Summary, t.Error, t.Turns, t.TokensUsed, t.CreatedAt, t.UpdatedAt, t.RequesterName);
}

public sealed record TaskEventDto(DateTimeOffset At, string Type, string Message);

public static class ApiEndpoints
{
    public static IServiceCollection AddAgentApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApiOptions>().Bind(configuration.GetSection(ApiOptions.SectionName));
        services.AddSingleton<CliConversationStore>();
        services.AddSingleton<Core.Conversations.IConversationContextProvider>(sp => sp.GetRequiredService<CliConversationStore>());
        services.AddHealthChecks();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured lazily so configuration layered on later (tests, environment) is honoured.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptionsMonitor<ApiOptions>, IOptionsMonitor<KeycloakOptions>>((o, apiOptions, keycloakOptions) =>
            {
                var api = apiOptions.CurrentValue;
                var keycloak = keycloakOptions.CurrentValue;

                o.MapInboundClaims = false;
                o.TokenValidationParameters.NameClaimType = "preferred_username";
                o.TokenValidationParameters.RoleClaimType = "roles";

                if (!string.IsNullOrWhiteSpace(api.DevSigningKey))
                {
                    // Tests and local development: no discovery document, symmetric key.
                    o.TokenValidationParameters.ValidateIssuer = !string.IsNullOrWhiteSpace(keycloak.Authority);
                    o.TokenValidationParameters.ValidIssuer = keycloak.Authority;
                    o.TokenValidationParameters.ValidateAudience = !string.IsNullOrWhiteSpace(keycloak.ApiAudience);
                    o.TokenValidationParameters.ValidAudience = keycloak.ApiAudience;
                    o.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(api.DevSigningKey));
                    o.RequireHttpsMetadata = false;
                }
                else
                {
                    o.Authority = keycloak.Authority;
                    o.Audience = keycloak.ApiAudience;
                    o.RequireHttpsMetadata = keycloak.Authority?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true;
                }
            });

        services.AddAuthorization();
        return services;
    }

    public static WebApplication MapAgentApi(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealthChecks("/healthz");
        app.MapHealthChecks("/readyz");

        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapPost("/chat", ChatAsync);
        api.MapDelete("/chat/{conversationId}", (string conversationId, CliConversationStore store) =>
        {
            store.Clear(conversationId);
            return Results.NoContent();
        });

        api.MapPost("/tasks", CreateTaskAsync);
        api.MapGet("/tasks", ListTasksAsync);
        api.MapGet("/tasks/{id:int}", GetTaskAsync);
        api.MapGet("/tasks/{id:int}/events", GetTaskEventsAsync);
        api.MapPost("/tasks/{id:int}/cancel", CancelTaskAsync);
        api.MapGet("/whoami", (HttpContext http, IRoleResolver roles, IOptions<KeycloakOptions> kc) =>
        {
            var caller = ApiCaller.From(http.User, roles, kc.Value.GroupClaim);
            return Results.Ok(new { caller.ChannelUserId, caller.Username, caller.Email, Roles = caller.Roles.Select(r => r.ToString()), caller.Groups });
        });

        return app;
    }

    private static async Task<IResult> ChatAsync(
        HttpContext http,
        [FromBody] ChatRequest request,
        IRoleResolver roles,
        IOptions<KeycloakOptions> keycloak,
        IAuthorizationService authorization,
        ICommandDispatcher commands,
        IAgent agent,
        CliConversationStore conversations,
        IServiceProvider services,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return Results.BadRequest(new { error = "text is required" });
        }

        var caller = ApiCaller.From(http.User, roles, keycloak.Value.GroupClaim);
        var conversationId = string.IsNullOrWhiteSpace(request.ConversationId) ? $"{caller.ChannelUserId}:{Guid.NewGuid():N}" : request.ConversationId;

        var evt = new InboundEvent
        {
            Channel = Channel.Cli,
            EventId = Guid.NewGuid().ToString("N"),
            Caller = caller,
            ConversationId = conversationId,
            Text = request.Text,
            IsPrivate = true,
        };

        using var scope = RequestContext.Begin(caller, evt);

        if (commands.IsCommand(request.Text))
        {
            var result = await commands.DispatchAsync(request.Text, new CommandContext
            {
                Caller = caller,
                Channel = Channel.Cli,
                ConversationId = conversationId,
                Event = evt,
                Services = services,
                CancellationToken = ct,
            });

            return Results.Ok(new ChatResponseDto(result.Markdown, conversationId, null, [], true, result.IsError));
        }

        var auth = await authorization.AuthorizeAsync(caller, Role.Users, "ask", ct);
        if (!auth.Allowed)
        {
            return Results.Forbid();
        }

        var agentRequest = new AgentRequest
        {
            Caller = caller,
            Channel = Channel.Cli,
            ConversationId = conversationId,
            Text = request.Text,
            History = conversations.Get(conversationId),
            ModelOverride = request.Model,
            ModelOverrideFromClient = true,
        };

        if (request.Stream || http.Request.Headers.Accept.Any(a => a?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true))
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Conversation-Id"] = conversationId;
            var full = new StringBuilder();

            await foreach (var chunk in agent.StreamAsync(agentRequest, ct))
            {
                full.Append(chunk);
                await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }

            conversations.Append(conversationId, request.Text, full.ToString(), caller.Username);
            await http.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
            return Results.Empty;
        }

        var response = await agent.AnswerAsync(agentRequest, ct);
        conversations.Append(conversationId, request.Text, response.Markdown, caller.Username);
        return Results.Ok(new ChatResponseDto(response.Markdown, conversationId, response.Model, response.ToolsUsed, false, false));
    }

    private static async Task<IResult> CreateTaskAsync(HttpContext http, [FromBody] CreateTaskRequest request, IRoleResolver roles, IOptions<KeycloakOptions> keycloak, IAuthorizationService authorization, ITaskService tasks, CancellationToken ct)
    {
        var caller = ApiCaller.From(http.User, roles, keycloak.Value.GroupClaim);
        var auth = await authorization.AuthorizeAsync(caller, Role.Team, "task.create", ct);
        if (!auth.Allowed)
        {
            return Results.Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.RepoUrl) || string.IsNullOrWhiteSpace(request.Instruction))
        {
            return Results.BadRequest(new { error = "repoUrl and instruction are required" });
        }

        var sourceRef = $"cli-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..28];
        var task = await tasks.CreateAsync(new TaskRequest
        {
            Source = TaskSource.Cli,
            SourceRef = sourceRef,
            Title = request.Title ?? FirstLine(request.Instruction),
            Requester = caller,
            Instruction = request.Instruction,
            RepoUrl = request.RepoUrl,
            ProjectId = request.ProjectId ?? ProjectPathFromUrl(request.RepoUrl),
            BaseBranch = request.BaseBranch,
            ConversationId = sourceRef,
            NotifyChannel = Channel.Cli,
        }, ct);

        return Results.Created($"/api/tasks/{task.Id}", TaskDto.From(task));
    }

    private static async Task<IResult> ListTasksAsync(HttpContext http, IRoleResolver roles, IOptions<KeycloakOptions> keycloak, ITaskService tasks, [FromQuery] bool all = false, [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        var caller = ApiCaller.From(http.User, roles, keycloak.Value.GroupClaim);
        if (all && !caller.HasRole(Role.Team))
        {
            return Results.Forbid();
        }

        var list = await tasks.ListAsync(new TaskQuery { RequesterId = all ? null : caller.Key, Limit = Math.Clamp(limit, 1, 200) }, ct);
        return Results.Ok(list.Select(TaskDto.From));
    }

    private static async Task<IResult> GetTaskAsync(HttpContext http, int id, IRoleResolver roles, IOptions<KeycloakOptions> keycloak, ITaskService tasks, CancellationToken ct)
    {
        var caller = ApiCaller.From(http.User, roles, keycloak.Value.GroupClaim);
        var task = await tasks.GetAsync(id, ct);
        if (task is null)
        {
            return Results.NotFound();
        }

        if (!Owns(caller, task) && !caller.HasRole(Role.Team))
        {
            return Results.Forbid();
        }

        return Results.Ok(TaskDto.From(task));
    }

    private static async Task<IResult> GetTaskEventsAsync(HttpContext http, int id, IRoleResolver roles, IOptions<KeycloakOptions> keycloak, ITaskService tasks, CancellationToken ct)
    {
        var caller = ApiCaller.From(http.User, roles, keycloak.Value.GroupClaim);
        var task = await tasks.GetAsync(id, ct);
        if (task is null)
        {
            return Results.NotFound();
        }

        if (!Owns(caller, task) && !caller.HasRole(Role.Team))
        {
            return Results.Forbid();
        }

        var events = await tasks.GetEventsAsync(id, 200, ct);
        return Results.Ok(events.Select(e => new TaskEventDto(e.At, e.Type, e.Message)));
    }

    private static async Task<IResult> CancelTaskAsync(HttpContext http, int id, IRoleResolver roles, IOptions<KeycloakOptions> keycloak, IAuthorizationService authorization, ITaskService tasks, CancellationToken ct)
    {
        var caller = ApiCaller.From(http.User, roles, keycloak.Value.GroupClaim);
        var auth = await authorization.AuthorizeAsync(caller, Role.Team, "task.cancel", ct);
        if (!auth.Allowed)
        {
            return Results.Forbid();
        }

        var task = await tasks.GetAsync(id, ct);
        if (task is null)
        {
            return Results.NotFound();
        }

        if (!Owns(caller, task) && !caller.HasRole(Role.Admin))
        {
            return Results.Forbid();
        }

        var cancelled = await tasks.CancelAsync(id, caller, ct);
        return Results.Ok(TaskDto.From(cancelled!));
    }

    private static bool Owns(CallerIdentity caller, AgentTask task) => string.Equals(task.RequesterId, caller.Key, StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length <= 80 ? line : line[..80];
    }

    internal static string? ProjectPathFromUrl(string repoUrl)
    {
        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        return path.Length == 0 ? null : path;
    }
}
