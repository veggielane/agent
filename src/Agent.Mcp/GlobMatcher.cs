namespace Agent.Mcp;

/// <summary>Case-insensitive wildcard matching: <c>*</c> matches any run of characters, <c>?</c> exactly one.</summary>
public static class GlobMatcher
{
    public static bool IsMatch(string pattern, string value)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(value);

        var p = 0;
        var v = 0;
        var starP = -1;
        var starV = -1;

        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], value[v])))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starV = v;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                v = ++starV;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    public static bool MatchesAny(IEnumerable<string> patterns, string value)
    {
        foreach (var pattern in patterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern) && IsMatch(pattern.Trim(), value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Same(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
