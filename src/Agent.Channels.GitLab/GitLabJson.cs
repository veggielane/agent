using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Channels.GitLab;

internal static class GitLabJson
{
    /// <summary>GitLab API v4 uses snake_case; ids are numbers but tolerate strings.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
