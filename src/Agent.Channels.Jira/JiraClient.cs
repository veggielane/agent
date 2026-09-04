using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Jira;

/// <summary>Thin REST v2 client for Jira Data Center.</summary>
public interface IJiraClient
{
    /// <summary>POST /rest/api/2/search.</summary>
    Task<JiraSearchResult> SearchAsync(string jql, IReadOnlyCollection<string>? fields, int startAt, int maxResults, CancellationToken cancellationToken);

    /// <summary>GET /rest/api/2/issue/{key}. Returns null when the issue does not exist or is not visible.</summary>
    Task<JiraIssue?> GetIssueAsync(string key, IReadOnlyCollection<string>? fields, CancellationToken cancellationToken);

    /// <summary>POST /rest/api/2/issue/{key}/comment with a wiki-markup body.</summary>
    Task<JiraComment> AddCommentAsync(string key, string body, CancellationToken cancellationToken);

    /// <summary>GET /rest/api/2/myself.</summary>
    Task<JiraUser> GetMyselfAsync(CancellationToken cancellationToken);
}

/// <summary>Raised for non-success Jira responses. <see cref="IsTransient"/> covers 429 and 5xx.</summary>
public sealed class JiraApiException : Exception
{
    public JiraApiException()
    {
    }

    public JiraApiException(string message)
        : base(message)
    {
    }

    public JiraApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public JiraApiException(HttpStatusCode statusCode, string message, string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }

    public string? ResponseBody { get; }

    public bool IsTransient => StatusCode == HttpStatusCode.TooManyRequests || (int)StatusCode >= 500;
}

public sealed class JiraClient : IJiraClient
{
    private const string ApiPrefix = "rest/api/2/";

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<JiraOptions> _options;
    private readonly ILogger<JiraClient> _logger;

    public JiraClient(HttpClient http, IOptionsMonitor<JiraOptions> options, ILogger<JiraClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<JiraSearchResult> SearchAsync(string jql, IReadOnlyCollection<string>? fields, int startAt, int maxResults, CancellationToken cancellationToken)
    {
        var body = new SearchRequest(jql, fields is { Count: > 0 } ? fields : null, Math.Max(0, startAt), Math.Clamp(maxResults, 1, 1000));
        using var request = CreateRequest(HttpMethod.Post, "search", body);
        return await SendAsync<JiraSearchResult>(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JiraIssue?> GetIssueAsync(string key, IReadOnlyCollection<string>? fields, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var path = "issue/" + Uri.EscapeDataString(key.Trim());
        if (fields is { Count: > 0 })
        {
            path += "?fields=" + Uri.EscapeDataString(string.Join(',', fields));
        }

        using var request = CreateRequest(HttpMethod.Get, path, null);
        try
        {
            return await SendAsync<JiraIssue>(request, cancellationToken).ConfigureAwait(false);
        }
        catch (JiraApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<JiraComment> AddCommentAsync(string key, string body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var request = CreateRequest(HttpMethod.Post, $"issue/{Uri.EscapeDataString(key.Trim())}/comment", new CommentRequest(body));
        return await SendAsync<JiraComment>(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JiraUser> GetMyselfAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "myself", null);
        return await SendAsync<JiraUser>(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the Authorization header for the configured credentials: Bearer for a token, Basic for a password.</summary>
    public static AuthenticationHeaderValue? CreateAuthorization(JiraOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Token))
        {
            return new AuthenticationHeaderValue("Bearer", options.Token.Trim());
        }

        if (!string.IsNullOrEmpty(options.Password))
        {
            var raw = Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}");
            return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        }

        return null;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath, object? body)
    {
        var options = _options.CurrentValue;
        var baseUri = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var request = new HttpRequestMessage(method, new Uri(baseUri, ApiPrefix + relativePath));
        request.Headers.Authorization = CreateAuthorization(options);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JiraJson.Options);
        }

        return request;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text = await ReadSafelyAsync(response, cancellationToken).ConfigureAwait(false);
            var message = $"Jira {request.Method} {request.RequestUri?.PathAndQuery} returned {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractErrorMessage(text)}".TrimEnd(':', ' ');
            _logger.LogDebug("{Message}", message);
            throw new JiraApiException(response.StatusCode, message, text);
        }

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            throw new JiraApiException(response.StatusCode, $"Jira {request.Method} {request.RequestUri?.PathAndQuery} returned no content.");
        }

        var result = await response.Content.ReadFromJsonAsync<T>(JiraJson.Options, cancellationToken).ConfigureAwait(false);
        return result ?? throw new JiraApiException(response.StatusCode, $"Jira {request.Method} {request.RequestUri?.PathAndQuery} returned an empty body.");
    }

    private static async Task<string> ReadSafelyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    /// <summary>Jira errors look like <c>{"errorMessages":["..."],"errors":{"field":"..."}}</c>.</summary>
    internal static string ExtractErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var parts = new List<string>();
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("errorMessages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                {
                    parts.AddRange(messages.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()!));
                }

                if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                {
                    parts.AddRange(errors.EnumerateObject().Select(p => $"{p.Name}: {p.Value}"));
                }

                if (parts.Count == 0 && doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    parts.Add(message.GetString()!);
                }
            }

            return parts.Count > 0 ? string.Join("; ", parts) : Shorten(body);
        }
        catch (JsonException)
        {
            return Shorten(body);
        }
    }

    private static string Shorten(string text) => text.Length <= 300 ? text.Trim() : text[..300].Trim() + "…";

    private sealed record SearchRequest(string Jql, IReadOnlyCollection<string>? Fields, int StartAt, int MaxResults);

    private sealed record CommentRequest(string Body);
}
