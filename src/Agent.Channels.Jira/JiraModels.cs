using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Agent.Channels.Jira;

public sealed record JiraSearchResult
{
    public int StartAt { get; init; }

    public int MaxResults { get; init; }

    public int Total { get; init; }

    public IReadOnlyList<JiraIssue> Issues { get; init; } = [];
}

public sealed record JiraIssue
{
    public string? Id { get; init; }

    public required string Key { get; init; }

    public string? Self { get; init; }

    public JiraFields Fields { get; init; } = new();
}

public sealed record JiraFields
{
    public string? Summary { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];

    public JiraStatus? Status { get; init; }

    public JiraProject? Project { get; init; }

    public IReadOnlyList<JiraComponent> Components { get; init; } = [];

    public JiraCommentPage? Comment { get; init; }

    public DateTimeOffset? Updated { get; init; }

    public DateTimeOffset? Created { get; init; }

    public JiraUser? Assignee { get; init; }

    public JiraUser? Reporter { get; init; }

    /// <summary>Custom fields (<c>customfield_NNNNN</c>) and anything else not modelled above.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Reads a custom field as a string. Handles plain strings and option objects with a <c>value</c>/<c>name</c>.</summary>
    public string? GetExtraString(string fieldId)
    {
        if (!Extra.TryGetValue(fieldId, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.Object when element.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String => v.GetString(),
            JsonValueKind.Object when element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String => n.GetString(),
            _ => null,
        };
    }
}

public sealed record JiraStatus
{
    public string? Id { get; init; }

    public string? Name { get; init; }
}

public sealed record JiraProject
{
    public string? Id { get; init; }

    public string? Key { get; init; }

    public string? Name { get; init; }
}

public sealed record JiraComponent
{
    public string? Id { get; init; }

    public string? Name { get; init; }
}

public sealed record JiraCommentPage
{
    public int StartAt { get; init; }

    public int MaxResults { get; init; }

    public int Total { get; init; }

    public IReadOnlyList<JiraComment> Comments { get; init; } = [];
}

public sealed record JiraComment
{
    public required string Id { get; init; }

    public JiraUser? Author { get; init; }

    public JiraUser? UpdateAuthor { get; init; }

    public string? Body { get; init; }

    public DateTimeOffset? Created { get; init; }

    public DateTimeOffset? Updated { get; init; }
}

public sealed record JiraUser
{
    /// <summary>Jira DC username.</summary>
    public string? Name { get; init; }

    public string? Key { get; init; }

    public string? EmailAddress { get; init; }

    public string? DisplayName { get; init; }

    public string? TimeZone { get; init; }

    public bool? Active { get; init; }

    [JsonIgnore]
    public string? Login => Name ?? Key;
}

/// <summary>Shared <see cref="JsonSerializerOptions"/>: camelCase, case-insensitive, Jira DC timestamps.</summary>
public static class JiraJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JiraDateTimeOffsetConverter() },
    };
}

/// <summary>Parses Jira DC timestamps such as <c>2024-05-01T10:15:30.000+0000</c> (offset without a colon).</summary>
public sealed partial class JiraDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return Parse(text) ?? throw new JsonException($"Invalid Jira timestamp '{text}'.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture));

    public static DateTimeOffset? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = CompactOffset().Replace(text.Trim(), "$1:$2");
        return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex CompactOffset();
}
