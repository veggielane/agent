using System.Text;

namespace Agent.Mcp;

/// <summary>Builds LLM-facing function names: <c>{server}__{tool}</c> restricted to <c>[A-Za-z0-9_-]{1,64}</c>.</summary>
public static class McpToolNaming
{
    public const int MaxLength = 64;

    public const string Separator = "__";

    public static string Build(string server, string tool) => Sanitise(server + Separator + tool);

    public static string Sanitise(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var sb = new StringBuilder(Math.Min(raw.Length, MaxLength));
        foreach (var c in raw)
        {
            if (sb.Length == MaxLength)
            {
                break;
            }

            sb.Append(IsAllowed(c) ? c : '_');
        }

        return sb.Length == 0 ? "_" : sb.ToString();
    }

    private static bool IsAllowed(char c)
        => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';
}
