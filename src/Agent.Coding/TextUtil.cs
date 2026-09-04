using System.Text;
using System.Text.RegularExpressions;

namespace Agent.Coding;

/// <summary>Small text helpers shared by tools, git plumbing, and the engine.</summary>
public static partial class TextUtil
{
    /// <summary>Cuts long text keeping the head and the tail, with a marker in the middle.</summary>
    public static string TruncateMiddle(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0 || text.Length <= maxChars)
        {
            return text ?? string.Empty;
        }

        var marker = $"\n…[{text.Length - maxChars} characters truncated]…\n";
        if (maxChars <= marker.Length + 2)
        {
            return text[..maxChars];
        }

        var keep = maxChars - marker.Length;
        var head = keep * 2 / 3;
        var tail = keep - head;
        return string.Concat(text.AsSpan(0, head), marker, text.AsSpan(text.Length - tail));
    }

    public static string TruncateEnd(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0 || text.Length <= maxChars)
        {
            return text ?? string.Empty;
        }

        return text[..maxChars] + $"\n…[{text.Length - maxChars} characters truncated]";
    }

    public static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        var idx = trimmed.IndexOfAny(['\r', '\n']);
        return idx < 0 ? trimmed : trimmed[..idx].Trim();
    }

    /// <summary>Lower-case, ASCII letters and digits, runs of anything else become a single dash.</summary>
    public static string Slug(string? text, int maxLength = 60)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var lastDash = true;
        foreach (var ch in text.ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }

            if (sb.Length >= maxLength)
            {
                break;
            }
        }

        return sb.ToString().Trim('-');
    }

    /// <summary>Heuristic binary detection: a NUL byte in the first few kilobytes.</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head)
        => head.IndexOf((byte)0) >= 0;

    [GeneratedRegex(@"\r\n|\r|\n")]
    public static partial Regex NewlineRegex();
}
