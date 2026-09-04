using Microsoft.Extensions.FileSystemGlobbing;

namespace Agent.Coding;

/// <summary>
/// Keeps every path a tool touches inside the workspace and enforces the protected-path globs.
/// Paths are compared after full normalisation; links inside the workspace are rejected as well.
/// </summary>
public sealed class PathGuard
{
    /// <summary>Always protected regardless of configuration: hooks and config inside .git would run as the orchestrator.</summary>
    public static readonly string[] BuiltInProtectedPaths = [".git", ".git/**", "**/.git/**"];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _rootWithSeparator;
    private readonly Matcher _protectedMatcher;
    private readonly List<string> _patterns;

    public PathGuard(string root, IEnumerable<string>? protectedGlobs = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _rootWithSeparator = Root + Path.DirectorySeparatorChar;
        _patterns = BuiltInProtectedPaths
            .Concat(protectedGlobs ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _protectedMatcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var pattern in _patterns)
        {
            _protectedMatcher.AddInclude(pattern);
        }
    }

    /// <summary>The workspace root as a full path without a trailing separator.</summary>
    public string Root { get; }

    public IReadOnlyList<string> ProtectedPatterns => _patterns;

    /// <summary>Resolves a workspace-relative path to a full path, or throws when it would leave the workspace.</summary>
    public string Resolve(string? relative)
    {
        var trimmed = (relative ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed == ".")
        {
            return Root;
        }

        if (trimmed.Contains(char.MinValue))
        {
            throw new CodingPolicyException("Path contains an invalid character.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(Root, trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CodingPolicyException($"Path '{relative}' is invalid: {ex.Message}");
        }

        full = Path.TrimEndingDirectorySeparator(full);
        if (!IsInside(full))
        {
            throw new CodingPolicyException($"Path '{relative}' is outside the workspace.");
        }

        RejectLinks(full);
        return full;
    }

    /// <summary>Converts a full path inside the workspace to the forward-slash relative form used in tool output.</summary>
    public string ToRelative(string fullPath)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        if (string.Equals(full, Root, PathComparison))
        {
            return string.Empty;
        }

        if (!IsInside(full))
        {
            throw new CodingPolicyException($"Path '{fullPath}' is outside the workspace.");
        }

        return full[_rootWithSeparator.Length..].Replace('\\', '/');
    }

    /// <summary>True when the (workspace-relative) path matches a protected glob. Escaping paths count as protected.</summary>
    public bool IsProtected(string? relative)
    {
        string normalised;
        try
        {
            normalised = ToRelative(Resolve(relative));
        }
        catch (CodingPolicyException)
        {
            return true;
        }

        if (normalised.Length == 0)
        {
            return true;
        }

        return _protectedMatcher.Match(normalised).HasMatches;
    }

    /// <summary>Throws when the path is protected; returns the resolved full path otherwise.</summary>
    public string ResolveForWrite(string? relative)
    {
        var full = Resolve(relative);
        if (string.Equals(full, Root, PathComparison))
        {
            throw new CodingPolicyException("A file path is required.");
        }

        var rel = ToRelative(full);
        if (_protectedMatcher.Match(rel).HasMatches)
        {
            throw new CodingPolicyException($"'{rel}' is a protected path and cannot be modified.");
        }

        return full;
    }

    private bool IsInside(string full)
        => string.Equals(full, Root, PathComparison) || full.StartsWith(_rootWithSeparator, PathComparison);

    private void RejectLinks(string full)
    {
        // Walk from the target up to (but excluding) the root; any existing reparse point means the real
        // location could be elsewhere, so refuse it.
        var current = full;
        while (!string.IsNullOrEmpty(current) && !string.Equals(current, Root, PathComparison))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                throw new CodingPolicyException($"'{ToRelative(current)}' is a link; links are not allowed inside the workspace.");
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }
    }
}
