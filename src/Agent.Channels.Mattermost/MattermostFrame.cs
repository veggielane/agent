using System.Text.Json;

namespace Agent.Channels.Mattermost;

/// <summary>A <c>posted</c> WebSocket event with the embedded post already parsed.</summary>
public sealed record MattermostPostedEvent(
    MattermostPost Post,
    string ChannelType,
    IReadOnlyList<string> Mentions,
    string? ChannelName = null,
    string? SenderName = null);

/// <summary>One WebSocket frame from Mattermost: an event, or the reply to an action such as the auth challenge.</summary>
public sealed record MattermostFrame(string? Event, string? Status, long? SeqReply, string? Error, MattermostPostedEvent? Posted)
{
    public const string PostedEvent = "posted";
    public const string HelloEvent = "hello";
    public const string OkStatus = "OK";

    public bool IsReply => Status is not null;

    public bool IsOk => string.Equals(Status, OkStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a frame. Throws <see cref="JsonException"/> on malformed JSON.</summary>
    public static MattermostFrame Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new MattermostFrame(null, null, null, null, null);
        }

        var evt = GetString(root, "event");
        var status = GetString(root, "status");
        long? seqReply = root.TryGetProperty("seq_reply", out var seq) && seq.ValueKind == JsonValueKind.Number ? seq.GetInt64() : null;
        var error = ReadError(root);

        MattermostPostedEvent? posted = null;
        if (string.Equals(evt, PostedEvent, StringComparison.Ordinal)
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object)
        {
            posted = ParsePosted(data);
        }

        return new MattermostFrame(evt, status, seqReply, error, posted);
    }

    private static MattermostPostedEvent? ParsePosted(JsonElement data)
    {
        var postJson = GetString(data, "post");
        if (string.IsNullOrWhiteSpace(postJson))
        {
            return null;
        }

        var post = JsonSerializer.Deserialize<MattermostPost>(postJson, MattermostJson.Options);
        if (post is null || string.IsNullOrEmpty(post.Id))
        {
            return null;
        }

        IReadOnlyList<string> mentions = [];
        var mentionsJson = GetString(data, "mentions");
        if (!string.IsNullOrWhiteSpace(mentionsJson))
        {
            try
            {
                mentions = JsonSerializer.Deserialize<string[]>(mentionsJson) ?? [];
            }
            catch (JsonException)
            {
                // Mentions are a hint; the mapper also looks at the text.
            }
        }

        return new MattermostPostedEvent(
            post,
            GetString(data, "channel_type") ?? string.Empty,
            mentions,
            GetString(data, "channel_name"),
            GetString(data, "sender_name"));
    }

    private static string? ReadError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error))
        {
            return null;
        }

        return error.ValueKind switch
        {
            JsonValueKind.Object => GetString(error, "message") ?? error.GetRawText(),
            JsonValueKind.String => error.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => error.GetRawText(),
        };
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
