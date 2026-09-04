using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Channels.Mattermost;

/// <summary>Serializer settings for Mattermost payloads: snake_case properties, nulls omitted.</summary>
public static class MattermostJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
