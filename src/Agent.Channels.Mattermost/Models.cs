using System.Text.Json.Serialization;

namespace Agent.Channels.Mattermost;

/// <summary>Mattermost channel type codes as they appear in <c>channel_type</c>.</summary>
public static class MattermostChannelTypes
{
    public const string Direct = "D";
    public const string Group = "G";
    public const string Open = "O";
    public const string Private = "P";
}

public sealed record MattermostUser
{
    public string Id { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string? Email { get; init; }

    public bool IsBot { get; init; }
}

public sealed record MattermostPost
{
    public string Id { get; init; } = string.Empty;

    /// <summary>Creation time in Unix milliseconds.</summary>
    public long CreateAt { get; init; }

    public long UpdateAt { get; init; }

    public string UserId { get; init; } = string.Empty;

    public string ChannelId { get; init; } = string.Empty;

    /// <summary>Thread root, or empty when the post is not a reply.</summary>
    public string RootId { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    /// <summary>Empty for normal posts; <c>system_*</c> for join/leave/header messages.</summary>
    public string Type { get; init; } = string.Empty;

    [JsonIgnore]
    public bool IsReply => !string.IsNullOrEmpty(RootId);

    [JsonIgnore]
    public bool IsSystemPost => !string.IsNullOrEmpty(Type);

    [JsonIgnore]
    public DateTimeOffset CreatedAt => DateTimeOffset.FromUnixTimeMilliseconds(CreateAt);
}

public sealed record MattermostChannel
{
    public string Id { get; init; } = string.Empty;

    /// <summary>D, G, O or P; see <see cref="MattermostChannelTypes"/>.</summary>
    public string Type { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>Shape of <c>/posts/{id}/thread</c> and <c>/channels/{id}/posts</c> responses.</summary>
public sealed record MattermostPostList
{
    public IReadOnlyList<string> Order { get; init; } = [];

    public IReadOnlyDictionary<string, MattermostPost> Posts { get; init; } = new Dictionary<string, MattermostPost>(StringComparer.Ordinal);
}

public sealed record MattermostReaction
{
    public string UserId { get; init; } = string.Empty;

    public string PostId { get; init; } = string.Empty;

    public string EmojiName { get; init; } = string.Empty;
}

internal sealed record CreatePostRequest(string ChannelId, string Message, string? RootId);

internal sealed record UpdatePostRequest(string Id, string Message);

internal sealed record ReactionRequest(string UserId, string PostId, string EmojiName);
