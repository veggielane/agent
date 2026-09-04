using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agent.Cli.Auth;

namespace Agent.Cli.Backends;

/// <summary>Talks to Agent.Host's /api with a Keycloak bearer token.</summary>
public sealed class RemoteBackend : IAgentBackend
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IAccessTokenProvider _tokens;

    public RemoteBackend(HttpClient http, IAccessTokenProvider tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    public string Description => $"remote {_http.BaseAddress}";

    public async Task<ChatResult> ChatAsync(string text, string? conversationId, string? model, CancellationToken cancellationToken)
    {
        using var request = await NewRequestAsync(HttpMethod.Post, "api/chat", cancellationToken);
        request.Content = JsonContent.Create(new { text, conversationId, model, stream = false });
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ChatResult>(Json, cancellationToken))!;
    }

    public async IAsyncEnumerable<string> StreamAsync(string text, string conversationId, string? model, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = await NewRequestAsync(HttpMethod.Post, "api/chat", cancellationToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = JsonContent.Create(new { text, conversationId, model, stream = true });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            // Commands and errors come back as JSON.
            var result = await response.Content.ReadFromJsonAsync<ChatResult>(Json, cancellationToken);
            yield return result?.Markdown ?? string.Empty;
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("event: done", StringComparison.Ordinal))
            {
                yield break;
            }

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var chunk = JsonSerializer.Deserialize<string>(line[6..]);
                if (!string.IsNullOrEmpty(chunk))
                {
                    yield return chunk;
                }
            }
        }
    }

    public async Task<TaskInfo> CreateTaskAsync(string repoUrl, string instruction, string? title, string? baseBranch, CancellationToken cancellationToken)
    {
        using var request = await NewRequestAsync(HttpMethod.Post, "api/tasks", cancellationToken);
        request.Content = JsonContent.Create(new { repoUrl, instruction, title, baseBranch });
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<TaskInfo>(Json, cancellationToken))!;
    }

    public async Task<IReadOnlyList<TaskInfo>> ListTasksAsync(bool all, int limit, CancellationToken cancellationToken)
    {
        using var request = await NewRequestAsync(HttpMethod.Get, $"api/tasks?all={all.ToString().ToLowerInvariant()}&limit={limit}", cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<TaskInfo>>(Json, cancellationToken) ?? [];
    }

    public async Task<TaskInfo?> GetTaskAsync(int id, CancellationToken cancellationToken)
    {
        using var request = await NewRequestAsync(HttpMethod.Get, $"api/tasks/{id}", cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TaskInfo>(Json, cancellationToken);
    }

    public async Task<IReadOnlyList<TaskEventInfo>> GetTaskEventsAsync(int id, CancellationToken cancellationToken)
    {
        using var request = await NewRequestAsync(HttpMethod.Get, $"api/tasks/{id}/events", cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<TaskEventInfo>>(Json, cancellationToken) ?? [];
    }

    public async Task<TaskInfo?> CancelTaskAsync(int id, CancellationToken cancellationToken)
    {
        using var request = await NewRequestAsync(HttpMethod.Post, $"api/tasks/{id}/cancel", cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TaskInfo>(Json, cancellationToken);
    }

    public async Task<WhoAmI> WhoAmIAsync(CancellationToken cancellationToken)
    {
        using var request = await NewRequestAsync(HttpMethod.Get, "api/whoami", cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var dto = await response.Content.ReadFromJsonAsync<WhoAmIDto>(Json, cancellationToken) ?? new WhoAmIDto();
        return new WhoAmI(dto.ChannelUserId ?? "?", dto.Username, dto.Email, dto.Roles ?? [], dto.Groups ?? []);
    }

    private async Task<HttpRequestMessage> NewRequestAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path);
        var token = await _tokens.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new CliException(response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Not authenticated. Run `agent login`.",
            System.Net.HttpStatusCode.Forbidden => "You are not authorised for that (check your roles with `agent whoami`).",
            _ => $"Server returned {(int)response.StatusCode}: {Shorten(body)}",
        });
    }

    private static string Shorten(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private sealed class WhoAmIDto
    {
        public string? ChannelUserId { get; set; }

        public string? Username { get; set; }

        public string? Email { get; set; }

        public List<string>? Roles { get; set; }

        public List<string>? Groups { get; set; }
    }
}

public sealed class CliException : Exception
{
    public CliException(string message)
        : base(message)
    {
    }
}
