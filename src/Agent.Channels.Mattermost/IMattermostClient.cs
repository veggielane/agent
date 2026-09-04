using System.Net;

namespace Agent.Channels.Mattermost;

/// <summary>The subset of the Mattermost REST API (v4) the channel needs.</summary>
public interface IMattermostClient
{
    /// <summary>The bot account itself.</summary>
    Task<MattermostUser> GetMeAsync(CancellationToken cancellationToken);

    /// <summary>Null when the user does not exist.</summary>
    Task<MattermostUser?> GetUserAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Null when the channel does not exist or the bot cannot see it.</summary>
    Task<MattermostChannel?> GetChannelAsync(string channelId, CancellationToken cancellationToken);

    /// <summary>Null when the post does not exist.</summary>
    Task<MattermostPost?> GetPostAsync(string postId, CancellationToken cancellationToken);

    /// <summary>The whole thread the post belongs to, oldest first.</summary>
    Task<IReadOnlyList<MattermostPost>> GetThreadAsync(string postId, CancellationToken cancellationToken);

    /// <summary>
    /// Posts of a channel, oldest first. <paramref name="since"/> (Unix ms) limits the result to posts modified
    /// after that time; <paramref name="perPage"/> limits the page when no <paramref name="since"/> is given.
    /// </summary>
    Task<IReadOnlyList<MattermostPost>> GetPostsForChannelAsync(string channelId, long? since, int? perPage, CancellationToken cancellationToken);

    Task<MattermostPost> CreatePostAsync(string channelId, string message, string? rootId, CancellationToken cancellationToken);

    Task<MattermostPost> UpdatePostAsync(string postId, string message, CancellationToken cancellationToken);

    Task AddReactionAsync(string userId, string postId, string emojiName, CancellationToken cancellationToken);

    Task RemoveReactionAsync(string userId, string postId, string emojiName, CancellationToken cancellationToken);
}

/// <summary>A non-success response from the Mattermost API.</summary>
public sealed class MattermostApiException : Exception
{
    public MattermostApiException(HttpStatusCode statusCode, string method, string path, string? body)
        : base($"Mattermost {method} {path} failed with {(int)statusCode}: {Shorten(body)}")
    {
        StatusCode = statusCode;
        ResponseBody = body;
    }

    public HttpStatusCode StatusCode { get; }

    public string? ResponseBody { get; }

    private static string Shorten(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty body)";
        }

        var text = body.Trim();
        return text.Length > 300 ? text[..300] + "..." : text;
    }
}
