using System.Text.Json;
using Agent.Channels.Jira;
using Agent.Core.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Agent.Channels.Jira.Tests;

internal static class TestOptions
{
    public static IOptionsMonitor<JiraOptions> Monitor(JiraOptions options)
    {
        var monitor = Substitute.For<IOptionsMonitor<JiraOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    public static JiraOptions For(WireMockServer server, Action<JiraOptions>? configure = null)
    {
        var options = new JiraOptions
        {
            BaseUrl = server.Url!,
            Username = "agent-bot",
            Token = "secret-token",
            BotUsername = "agent-bot",
            Projects = ["PROJ"],
            PollSeconds = 1,
            OverlapMinutes = 2,
            TimeZone = "UTC",
        };

        configure?.Invoke(options);
        return options;
    }

    public static JiraOptions Plain(Action<JiraOptions>? configure = null)
    {
        var options = new JiraOptions
        {
            BaseUrl = "https://jira.test",
            Username = "agent-bot",
            Token = "secret-token",
            BotUsername = "agent-bot",
            Projects = ["PROJ"],
        };

        configure?.Invoke(options);
        return options;
    }

    public static JiraClient Client(WireMockServer server, JiraOptions options)
        => new(new HttpClient(), Monitor(options), NullLogger<JiraClient>.Instance);
}

/// <summary>Builds Jira REST payloads with camelCase names, the way Jira DC returns them.</summary>
internal static class Fx
{
    public const string T1000 = "2024-05-01T10:00:00.000+0000";
    public const string T1005 = "2024-05-01T10:05:00.000+0000";
    public const string T1010 = "2024-05-01T10:10:00.000+0000";
    public const string T1017 = "2024-05-01T10:17:00.000+0000";
    public const string T1018 = "2024-05-01T10:18:30.000+0000";
    public const string T1019 = "2024-05-01T10:19:00.000+0000";

    public static readonly DateTimeOffset Now = new(2024, 5, 1, 10, 20, 0, TimeSpan.Zero);

    public static object User(string name, string? email = null, string? displayName = null)
        => new { name, key = name, emailAddress = email ?? $"{name}@corp.local", displayName = displayName ?? name };

    public static object Comment(string id, string author, string body, string created)
        => new { id, author = User(author), body, created, updated = created };

    public static object Issue(
        string key,
        string summary,
        string? description = null,
        string[]? labels = null,
        object[]? comments = null,
        string updated = T1019,
        string? reporter = "alice",
        string? assignee = null,
        string[]? components = null,
        string status = "Open",
        IDictionary<string, object?>? extra = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["summary"] = summary,
            ["description"] = description,
            ["labels"] = labels ?? [],
            ["status"] = new { id = "1", name = status },
            ["project"] = new { id = "10000", key = key[..key.LastIndexOf('-')], name = "Project" },
            ["components"] = (components ?? []).Select(c => new { id = c, name = c }).ToArray(),
            ["comment"] = new { startAt = 0, maxResults = comments?.Length ?? 0, total = comments?.Length ?? 0, comments = comments ?? [] },
            ["updated"] = updated,
            ["created"] = T1000,
            ["reporter"] = reporter is null ? null : User(reporter),
            ["assignee"] = assignee is null ? null : User(assignee),
        };

        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                fields[pair.Key] = pair.Value;
            }
        }

        return new { id = key.GetHashCode(StringComparison.Ordinal).ToString(System.Globalization.CultureInfo.InvariantCulture), key, fields };
    }

    public static object SearchResult(params object[] issues) => SearchResult(issues.Length, 0, issues);

    public static object SearchResult(int total, int startAt, params object[] issues)
        => new { startAt, maxResults = 50, total, issues };

    public static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JiraIssue Parse(object issue) => JsonSerializer.Deserialize<JiraIssue>(Json(issue), JiraJson.Options)!;

    public static DateTimeOffset At(string jiraTimestamp) => JiraDateTimeOffsetConverter.Parse(jiraTimestamp)!.Value;
}

internal static class Stubs
{
    public static void Myself(WireMockServer server, string timeZone = "UTC")
        => server.Given(Request.Create().WithPath("/rest/api/2/myself").UsingGet())
            .RespondWith(Json(new { name = "agent-bot", key = "agent-bot", emailAddress = "agent-bot@corp.local", displayName = "Agent Bot", timeZone }));

    public static void Search(WireMockServer server, object result)
        => server.Given(Request.Create().WithPath("/rest/api/2/search").UsingPost()).RespondWith(Json(result));

    public static void Issue(WireMockServer server, string key, object issue)
        => server.Given(Request.Create().WithPath($"/rest/api/2/issue/{key}").UsingGet()).RespondWith(Json(issue));

    public static void Comment(WireMockServer server, string key)
        => server.Given(Request.Create().WithPath($"/rest/api/2/issue/{key}/comment").UsingPost())
            .RespondWith(Json(new { id = "9001", author = Fx.User("agent-bot"), body = "echo", created = Fx.T1019 }));

    public static IResponseBuilder Json(object body, int status = 200)
        => Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(Fx.Json(body));

    public static IResponseBuilder Error(int status, params string[] messages)
        => Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(Fx.Json(new { errorMessages = messages, errors = new { } }));

    public static string? Header(this WireMock.IRequestMessage? request, string name)
        => request?.Headers is not null && request.Headers.TryGetValue(name, out var values) ? string.Join(",", values) : null;

    public static JsonElement JsonBody(this WireMock.IRequestMessage? request)
        => JsonDocument.Parse(request?.Body ?? "{}").RootElement;

    public static IEnumerable<WireMock.IRequestMessage> Requests(this WireMockServer server)
        => server.LogEntries.Select(e => e.RequestMessage!);
}

internal sealed class FakeTimeProvider : TimeProvider
{
    public FakeTimeProvider(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class Queues
{
    public static async Task<List<InboundEvent>> DrainAsync(IInboundQueue queue, int count, CancellationToken cancellationToken)
    {
        var events = new List<InboundEvent>();
        if (count <= 0)
        {
            return events;
        }

        await foreach (var evt in queue.ReadAllAsync(cancellationToken))
        {
            events.Add(evt);
            if (events.Count == count)
            {
                break;
            }
        }

        return events;
    }
}
