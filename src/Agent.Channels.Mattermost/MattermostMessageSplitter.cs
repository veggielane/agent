using System.Text;

namespace Agent.Channels.Mattermost;

/// <summary>
/// Splits a long reply into posts that fit the server limit. Splits happen at line breaks and never inside a
/// <c>```</c> code fence: an open fence is closed at the end of a chunk and reopened at the start of the next.
/// A fence that was opened as the last line of a chunk moves to the next chunk instead of leaving an empty block.
/// </summary>
public static class MattermostMessageSplitter
{
    private const string FenceMarker = "```";
    private const string FenceClose = "\n```";

    public static IReadOnlyList<string> Split(string text, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, FenceClose.Length + 2);
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (text.Length <= maxLength)
        {
            return [text];
        }

        var chunks = new List<string>();
        var current = new StringBuilder();
        string? openFence = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var isFenceLine = line.TrimStart().StartsWith(FenceMarker, StringComparison.Ordinal);
            var closesFence = isFenceLine && openFence is not null;
            var fenceAfterLine = isFenceLine
                ? (openFence is null ? line : null)
                : openFence;
            var reserve = fenceAfterLine is null ? 0 : FenceClose.Length;

            if (current.Length > 0 && current.Length + 1 + line.Length + reserve > maxLength)
            {
                Flush(chunks, current, openFence, reopen: !closesFence);
                if (closesFence)
                {
                    // The chunk was closed with a fence already; the closing line itself is not needed.
                    openFence = null;
                    continue;
                }
            }

            AppendLine(chunks, current, line, openFence, reserve, maxLength);
            if (isFenceLine)
            {
                openFence = fenceAfterLine;
            }
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    private static void AppendLine(List<string> chunks, StringBuilder current, string line, string? openFence, int reserve, int maxLength)
    {
        var remaining = line.AsSpan();
        while (true)
        {
            var separator = current.Length > 0 ? 1 : 0;
            var room = maxLength - current.Length - separator - reserve;
            if (remaining.Length <= room)
            {
                if (separator == 1)
                {
                    current.Append('\n');
                }

                current.Append(remaining);
                return;
            }

            if (room <= 0)
            {
                if (openFence is not null && IsOnlyOpener(current, openFence))
                {
                    // The fence opener alone fills the post; take what fits without the reserve so we still progress.
                    room = Math.Max(1, maxLength - current.Length - separator);
                }
                else
                {
                    Flush(chunks, current, openFence, reopen: true);
                    continue;
                }
            }

            // A single line longer than a post: hard-split it.
            if (separator == 1)
            {
                current.Append('\n');
            }

            current.Append(remaining[..room]);
            remaining = remaining[room..];
            Flush(chunks, current, openFence, reopen: true);
        }
    }

    private static void Flush(List<string> chunks, StringBuilder current, string? openFence, bool reopen)
    {
        if (openFence is not null)
        {
            if (EndsWithOpener(current, openFence))
            {
                // The fence was opened as the last line; carry it over instead of emitting an empty block.
                current.Length -= openFence.Length;
                if (current.Length > 0 && current[^1] == '\n')
                {
                    current.Length -= 1;
                }

                if (current.Length > 0)
                {
                    chunks.Add(current.ToString());
                }

                current.Clear();
                if (reopen)
                {
                    current.Append(openFence);
                }

                return;
            }

            current.Append(FenceClose);
        }

        chunks.Add(current.ToString());
        current.Clear();
        if (openFence is not null && reopen)
        {
            current.Append(openFence);
        }
    }

    private static bool IsOnlyOpener(StringBuilder current, string openFence)
        => current.Length == openFence.Length && current.Equals(openFence.AsSpan());

    private static bool EndsWithOpener(StringBuilder current, string openFence)
    {
        if (current.Length < openFence.Length)
        {
            return false;
        }

        var start = current.Length - openFence.Length;
        for (var i = 0; i < openFence.Length; i++)
        {
            if (current[start + i] != openFence[i])
            {
                return false;
            }
        }

        return start == 0 || current[start - 1] == '\n';
    }
}
