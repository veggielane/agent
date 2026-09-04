using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Mattermost;

/// <summary>Typed <see cref="HttpClient"/> for the Mattermost REST API. Sends the bot token as a bearer header.</summary>
public sealed class MattermostClient : IMattermostClient
{
    private readonly HttpClient _http;

    public MattermostClient(HttpClient http, IOptionsMonitor<MattermostOptions> options)
    {
        _http = http;
        var current = options.CurrentValue;
        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(current.BaseUrl))
        {
            _http.BaseAddress = NormalizeBaseUrl(current.BaseUrl);
        }

        if (!string.IsNullOrWhiteSpace(current.BotToken))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", current.BotToken);
        }
    }

    /// <summary>Ensures a trailing slash so relative API paths resolve under the configured path.</summary>
    public static Uri NormalizeBaseUrl(string baseUrl) => new(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute);

    public Task<MattermostUser> GetMeAsync(CancellationToken cancellationToken)
        => GetRequiredAsync<MattermostUser>("api/v4/users/me", cancellationToken);

    public Task<MattermostUser?> GetUserAsync(string userId, CancellationToken cancellationToken)
        => GetOptionalAsync<MattermostUser>($"api/v4/users/{Escape(userId)}", cancellationToken);

    public Task<MattermostChannel?> GetChannelAsync(string channelId, CancellationToken cancellationToken)
        => GetOptionalAsync<MattermostChannel>($"api/v4/channels/{Escape(channelId)}", cancellationToken);

    public Task<MattermostPost?> GetPostAsync(string postId, CancellationToken cancellationToken)
        => GetOptionalAsync<MattermostPost>($"api/v4/posts/{Escape(postId)}", cancellationToken);

    public async Task<IReadOnlyList<MattermostPost>> GetThreadAsync(string postId, CancellationToken cancellationToken)
    {
        var list = await GetRequiredAsync<MattermostPostList>($"api/v4/posts/{Escape(postId)}/thread", cancellationToken).ConfigureAwait(false);
        return Ordered(list);
    }

    public async Task<IReadOnlyList<MattermostPost>> GetPostsForChannelAsync(string channelId, long? since, int? perPage, CancellationToken cancellationToken)
    {
        var query = new List<string>(2);
        if (since is > 0)
        {
            query.Add("since=" + since.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (perPage is > 0)
        {
            query.Add("per_page=" + perPage.Value.ToString(CultureInfo.InvariantCulture));
        }

        var path = $"api/v4/channels/{Escape(channelId)}/posts";
        if (query.Count > 0)
        {
            path += "?" + string.Join('&', query);
        }

        var list = await GetRequiredAsync<MattermostPostList>(path, cancellationToken).ConfigureAwait(false);
        return Ordered(list);
    }

    public Task<MattermostPost> CreatePostAsync(string channelId, string message, string? rootId, CancellationToken cancellationToken)
        => SendAsync<CreatePostRequest, MattermostPost>(
            HttpMethod.Post,
            "api/v4/posts",
            new CreatePostRequest(channelId, message, string.IsNullOrEmpty(rootId) ? null : rootId),
            cancellationToken);

    public Task<MattermostPost> UpdatePostAsync(string postId, string message, CancellationToken cancellationToken)
        => SendAsync<UpdatePostRequest, MattermostPost>(
            HttpMethod.Put,
            $"api/v4/posts/{Escape(postId)}",
            new UpdatePostRequest(postId, message),
            cancellationToken);

    public Task AddReactionAsync(string userId, string postId, string emojiName, CancellationToken cancellationToken)
        => SendAsync<ReactionRequest, MattermostReaction>(
            HttpMethod.Post,
            "api/v4/reactions",
            new ReactionRequest(userId, postId, emojiName),
            cancellationToken);

    public async Task RemoveReactionAsync(string userId, string postId, string emojiName, CancellationToken cancellationToken)
    {
        var path = $"api/v4/users/{Escape(userId)}/posts/{Escape(postId)}/reactions/{Escape(emojiName)}";
        using var response = await _http.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "DELETE", path, cancellationToken).ConfigureAwait(false);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static IReadOnlyList<MattermostPost> Ordered(MattermostPostList list)
        => list.Posts.Values
            .OrderBy(p => p.CreateAt)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

    private async Task<T> GetRequiredAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "GET", path, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, "GET", path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetOptionalAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "GET", path, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, "GET", path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(HttpMethod method, string path, TRequest body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: MattermostJson.Options),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, method.Method, path, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<TResponse>(response, method.Method, path, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string method, string path, CancellationToken cancellationToken)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(MattermostJson.Options, cancellationToken).ConfigureAwait(false);
        return value ?? throw new MattermostApiException(response.StatusCode, method, path, "empty JSON body");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string method, string path, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The status code is the useful part; a missing body is fine.
        }

        throw new MattermostApiException(response.StatusCode, method, path, body);
    }
}
