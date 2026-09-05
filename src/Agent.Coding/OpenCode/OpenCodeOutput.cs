using System.Text;
using System.Text.Json;

namespace Agent.Coding.OpenCode;

public sealed record OpenCodeRunOutput(string Summary, long InputTokens, long OutputTokens, int Messages)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// Reads what <c>opencode run --format json</c> printed. The event schema belongs to opencode and can
/// change, so this is deliberately tolerant: it harvests any assistant text and any token counts it
/// recognises, and falls back to the raw output when it recognises nothing. It never throws.
/// </summary>
public static class OpenCodeOutput
{
    private static readonly string[] TextKeys = ["text", "content", "message", "summary"];
    private static readonly string[] InputTokenKeys = ["input", "input_tokens", "inputTokens", "prompt_tokens", "promptTokens"];
    private static readonly string[] OutputTokenKeys = ["output", "output_tokens", "outputTokens", "completion_tokens", "completionTokens"];

    public static OpenCodeRunOutput Parse(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return new OpenCodeRunOutput(string.Empty, 0, 0, 0);
        }

        var text = new StringBuilder();
        long input = 0;
        long output = 0;
        var messages = 0;

        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is not ('{' or '['))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(trimmed);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                messages++;
                Harvest(document.RootElement, text, ref input, ref output, depth: 0);
            }
        }

        // Nothing recognisable: hand back what it printed rather than an empty summary.
        var summary = text.Length > 0 ? text.ToString().Trim() : stdout.Trim();
        return new OpenCodeRunOutput(summary, input, output, messages);
    }

    private static void Harvest(JsonElement element, StringBuilder text, ref long input, ref long output, int depth)
    {
        if (depth > 12)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String
                        && TextKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                        && property.Value.GetString() is { Length: > 0 } value)
                    {
                        if (text.Length > 0)
                        {
                            text.Append('\n');
                        }

                        text.Append(value);
                        continue;
                    }

                    if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        if (InputTokenKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && property.Value.TryGetInt64(out var i))
                        {
                            input += i;
                            continue;
                        }

                        if (OutputTokenKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && property.Value.TryGetInt64(out var o))
                        {
                            output += o;
                            continue;
                        }
                    }

                    Harvest(property.Value, text, ref input, ref output, depth + 1);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Harvest(item, text, ref input, ref output, depth + 1);
                }

                break;
        }
    }
}
