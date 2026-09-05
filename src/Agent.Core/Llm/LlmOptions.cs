using System.ComponentModel.DataAnnotations;

namespace Agent.Core.Llm;

public enum ModelPurpose
{
    Answer,
    Coding,
}

/// <summary>
/// Bound from the <c>Llm</c> section and read through <c>IOptionsMonitor</c>, so edits to appsettings switch
/// models without a restart.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>OpenAI-compatible base URL, e.g. https://llm.internal/v1</summary>
    [Required]
    public string BaseUrl { get; set; } = string.Empty;

    public string? ApiKey { get; set; }

    [Required]
    public string AnswerModel { get; set; } = string.Empty;

    /// <summary>Defaults to <see cref="AnswerModel"/> when empty.</summary>
    public string? CodingModel { get; set; }

    /// <summary>Per purpose/channel overrides, e.g. "Jira.Answer": "small-model", "Coding": "big-model".</summary>
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the API honours a model supplied by the CLI.</summary>
    public bool AllowClientModelOverride { get; set; }

    [Range(5, 3600)]
    public int TimeoutSeconds { get; set; } = 120;

    [Range(1, 200_000)]
    public int MaxOutputTokens { get; set; } = 1500;

    /// <summary>Upper bound on tool-call round trips per answer.</summary>
    [Range(1, 200)]
    public int MaxToolIterations { get; set; } = 20;

    public float? Temperature { get; set; }

    /// <summary>
    /// Records prompts and completions on the gen_ai telemetry spans. Off by default: those carry ticket
    /// and repository content, which does not belong in a tracing backend by accident.
    /// </summary>
    public bool EnableSensitiveTelemetry { get; set; }

    public string ResolveModel(ModelPurpose purpose, string? overrideKey = null, string? explicitModel = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitModel))
        {
            return explicitModel;
        }

        if (overrideKey is not null && Overrides.TryGetValue(overrideKey, out var m) && !string.IsNullOrWhiteSpace(m))
        {
            return m;
        }

        if (Overrides.TryGetValue(purpose.ToString(), out var byPurpose) && !string.IsNullOrWhiteSpace(byPurpose))
        {
            return byPurpose;
        }

        return purpose == ModelPurpose.Coding && !string.IsNullOrWhiteSpace(CodingModel) ? CodingModel : AnswerModel;
    }
}
