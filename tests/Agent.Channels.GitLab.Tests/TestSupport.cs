using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Channels.GitLab;
using Agent.Core.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock;

namespace Agent.Channels.GitLab.Tests;

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal static class TestOptions
{
    public static GitLabOptions Default(string baseUrl = "https://gitlab.test") => new()
    {
        BaseUrl = baseUrl,
        Token = "glpat-test-token",
        BotUsername = "agent-bot",
        PollSeconds = 30,
        OverlapMinutes = 2,
        Groups = [],
        TaskLabel = "agent",
        TaskOnAssign = true,
    };

    public static IOptionsMonitor<GitLabOptions> Monitor(GitLabOptions options) => new StaticOptionsMonitor<GitLabOptions>(options);
}

/// <summary>Serializes anonymous objects the way GitLab does (snake_case, nulls omitted).</summary>
internal static class Json
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Of(object value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>GitLab API payload builders shared by the client, poller and tool tests.</summary>
internal static class Payloads
{
    public static object UserRef(long id, string username) => new { Id = id, Username = username, Name = username, State = "active" };

    public static object User(long id, string username, string? email = null, string? ldapDn = null) => new
    {
        Id = id,
        Username = username,
        Name = username,
        Email = email,
        State = "active",
        Identities = ldapDn is null ? Array.Empty<object>() : [new { Provider = "ldapmain", ExternUid = ldapDn }],
    };

    public static object ProjectRef(long id, string path) => new
    {
        Id = id,
        Name = path.Split('/').Last(),
        Path = path.Split('/').Last(),
        PathWithNamespace = path,
    };

    public static object Project(long id, string path, string defaultBranch = "main", string host = "https://gitlab.test") => new
    {
        Id = id,
        Name = path.Split('/').Last(),
        Path = path.Split('/').Last(),
        PathWithNamespace = path,
        DefaultBranch = defaultBranch,
        HttpUrlToRepo = $"{host}/{path}.git",
        WebUrl = $"{host}/{path}",
    };

    public static object IssueTarget(long iid, long projectId, string path, string title, string? description, object author) => new
    {
        Id = projectId * 1000 + iid,
        Iid = iid,
        ProjectId = projectId,
        Title = title,
        Description = description,
        State = "opened",
        Author = author,
        Labels = new[] { "agent" },
        WebUrl = $"https://gitlab.test/{path}/-/issues/{iid}",
    };

    public static object MergeRequestTarget(long iid, long projectId, string path, string title, string sourceBranch, string targetBranch, object author) => new
    {
        Id = projectId * 1000 + iid,
        Iid = iid,
        ProjectId = projectId,
        Title = title,
        Description = "MR body",
        State = "opened",
        Author = author,
        Labels = new[] { "agent" },
        SourceBranch = sourceBranch,
        TargetBranch = targetBranch,
        WebUrl = $"https://gitlab.test/{path}/-/merge_requests/{iid}",
    };

    public static object Todo(long id, string action, string targetType, object project, object author, object target, string? targetUrl, string? body) => new
    {
        Id = id,
        Project = project,
        Author = author,
        ActionName = action,
        TargetType = targetType,
        Target = target,
        TargetUrl = targetUrl,
        Body = body,
        State = "pending",
        CreatedAt = "2026-09-04T08:00:00.000Z",
        UpdatedAt = "2026-09-04T08:00:00.000Z",
    };

    public static object Issue(long iid, long projectId, string path, string title, string? description, object author, string updatedAt, string[]? labels = null) => new
    {
        Id = projectId * 1000 + iid,
        Iid = iid,
        ProjectId = projectId,
        Title = title,
        Description = description,
        State = "opened",
        Author = author,
        Labels = labels ?? ["agent"],
        WebUrl = $"https://gitlab.test/{path}/-/issues/{iid}",
        CreatedAt = "2026-09-01T08:00:00.000Z",
        UpdatedAt = updatedAt,
        References = new { Short = $"#{iid}", Relative = $"#{iid}", Full = $"{path}#{iid}" },
    };

    public static object MergeRequest(long iid, long projectId, string path, string title, string state, string sourceBranch, string targetBranch, object author, string? description = null, bool draft = false, string[]? labels = null) => new
    {
        Id = projectId * 1000 + iid,
        Iid = iid,
        ProjectId = projectId,
        Title = title,
        Description = description ?? "MR body",
        State = state,
        SourceBranch = sourceBranch,
        TargetBranch = targetBranch,
        WebUrl = $"https://gitlab.test/{path}/-/merge_requests/{iid}",
        Author = author,
        Labels = labels ?? ["agent"],
        Draft = draft,
        MergedAt = state == "merged" ? "2026-09-04T09:00:00.000Z" : null,
        CreatedAt = "2026-09-01T08:00:00.000Z",
        UpdatedAt = "2026-09-04T08:00:00.000Z",
    };

    public static object Note(long id, string body, object author, string createdAt, bool system = false, object? position = null, string? type = null) => new
    {
        Id = id,
        Type = type,
        Body = body,
        Author = author,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        System = system,
        NoteableType = "Issue",
        Position = position,
    };

    public static object Position(string newPath, int newLine, string? oldPath = null, int? oldLine = null) => new
    {
        BaseSha = "a",
        StartSha = "b",
        HeadSha = "c",
        OldPath = oldPath ?? newPath,
        NewPath = newPath,
        PositionType = "text",
        OldLine = oldLine,
        NewLine = newLine,
    };

    public static object Discussion(string id, bool individualNote, params object[] notes) => new
    {
        Id = id,
        IndividualNote = individualNote,
        Notes = notes,
    };
}

/// <summary>A WireMock server plus a <see cref="GitLabClient"/> pointed at it.</summary>
internal sealed class GitLabTestServer : IDisposable
{
    public const string Token = "glpat-test-token";

    public GitLabTestServer()
    {
        Server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(Server.Url! + "/") };
        http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", Token);
        Client = new GitLabClient(http);
    }

    public WireMockServer Server { get; }

    public GitLabClient Client { get; }

    public string Url => Server.Url!;

    public void Get(string path, object body, params (string Name, string Value)[] query)
        => Get(path, Json.Of(body), query);

    public void Get(string path, string json, params (string Name, string Value)[] query)
    {
        var request = Request.Create().WithPath(path).UsingGet();
        foreach (var (name, value) in query)
        {
            request = request.WithParam(name, value);
        }

        Server.Given(request).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(json));
    }

    public void GetStatus(string path, int status, string body = "{\"message\":\"404 Not Found\"}")
        => Server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    public void Post(string path, object body, int status = 201)
        => Server.Given(Request.Create().WithPath(path).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(Json.Of(body)));

    public void Put(string path, object body, int status = 200)
        => Server.Given(Request.Create().WithPath(path).UsingPut())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(Json.Of(body)));

    /// <summary>Recorded requests with the given method whose URL contains <paramref name="pathContains"/>, in order.</summary>
    public IReadOnlyList<IRequestMessage> Requests(string method, string pathContains)
        => Server.LogEntries
            .Select(e => e.RequestMessage)
            .Where(r => r is not null && string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase) && r.Url is not null && r.Url.Contains(pathContains, StringComparison.Ordinal))
            .Select(r => r!)
            .ToList();

    public void Dispose()
    {
        Server.Stop();
        Server.Dispose();
    }
}

internal static class QueueExtensions
{
    /// <summary>Reads exactly the events currently queued without waiting for more.</summary>
    public static async Task<List<InboundEvent>> DrainAsync(this IInboundQueue queue)
    {
        var expected = queue.Depth;
        var events = new List<InboundEvent>(expected);
        if (expected == 0)
        {
            return events;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in queue.ReadAllAsync(cts.Token))
        {
            events.Add(evt);
            if (events.Count >= expected)
            {
                break;
            }
        }

        return events;
    }
}

/// <summary>A <see cref="TimeProvider"/> with a fixed clock; timers still use the system scheduler.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class Loggers
{
    public static NullLogger<T> For<T>() => NullLogger<T>.Instance;
}
