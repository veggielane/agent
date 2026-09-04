namespace Agent.Coding;

public sealed record ParsedCommand(string Executable, IReadOnlyList<string> Arguments)
{
    public override string ToString() => string.Join(' ', new[] { Executable }.Concat(Arguments.Select(Quote)));

    private static string Quote(string arg) => arg.Length > 0 && !arg.Any(char.IsWhiteSpace) ? arg : $"\"{arg}\"";
}

public sealed record CommandToken(string Text, bool Quoted);

/// <summary>
/// Turns a command line from the model into an executable plus arguments and checks it against the allow list.
/// There is no shell: the executable is started directly, so shell operators are rejected outright.
/// </summary>
public sealed class CommandPolicy
{
    private static readonly char[] ShellOperatorChars = ['|', '&', ';', '>', '<', '`', '\n', '\r'];
    private static readonly char[] PathChars = ['/', '\\', ':'];
    private static readonly string[] ExecutableSuffixes = [".exe", ".cmd", ".bat", ".com"];

    private readonly HashSet<string> _allowed;
    private readonly HashSet<string> _allowedGitSubcommands;

    public CommandPolicy(IEnumerable<string> allowedExecutables, IEnumerable<string>? allowedGitSubcommands = null)
    {
        _allowed = new HashSet<string>(allowedExecutables.Select(Normalise).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        _allowedGitSubcommands = new HashSet<string>(
            (allowedGitSubcommands ?? CodingOptions.DefaultAllowedGitSubcommands).Select(s => s.Trim()).Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> AllowedExecutables => _allowed;

    /// <summary>Parses and validates; throws <see cref="CodingPolicyException"/> for anything that is not allowed.</summary>
    public ParsedCommand Parse(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new CodingPolicyException("Command is empty.");
        }

        if (commandLine.Contains('\n') || commandLine.Contains('\r'))
        {
            throw new CodingPolicyException("Command must be a single line.");
        }

        var tokens = Tokenize(commandLine);
        if (tokens.Count == 0)
        {
            throw new CodingPolicyException("Command is empty.");
        }

        foreach (var token in tokens)
        {
            if (!token.Quoted && (token.Text.IndexOfAny(ShellOperatorChars) >= 0 || token.Text.Contains("$(", StringComparison.Ordinal)))
            {
                throw new CodingPolicyException($"Shell operators are not supported ('{token.Text}'); run one executable at a time.");
            }
        }

        var exeToken = tokens[0].Text;
        if (exeToken.IndexOfAny(PathChars) >= 0)
        {
            throw new CodingPolicyException($"'{exeToken}' must be a bare executable name resolved through PATH, not a path.");
        }

        var exe = Normalise(exeToken);
        if (!_allowed.Contains(exe))
        {
            throw new CodingPolicyException($"'{exe}' is not an allowed executable. Allowed: {string.Join(", ", _allowed.Order(StringComparer.OrdinalIgnoreCase))}.");
        }

        var args = tokens.Skip(1).Select(t => t.Text).ToList();
        if (exe.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            CheckGit(args);
        }

        return new ParsedCommand(exeToken, args);
    }

    /// <summary>Splits on whitespace honouring double and single quotes. Backslashes are literal (Windows paths).</summary>
    public static IReadOnlyList<CommandToken> Tokenize(string commandLine)
    {
        var tokens = new List<CommandToken>();
        var current = new System.Text.StringBuilder();
        var inToken = false;
        var quoted = false;
        char? quote = null;

        foreach (var ch in commandLine)
        {
            if (quote is not null)
            {
                if (ch == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                inToken = true;
                quoted = true;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (inToken)
                {
                    tokens.Add(new CommandToken(current.ToString(), quoted));
                    current.Clear();
                    inToken = false;
                    quoted = false;
                }

                continue;
            }

            inToken = true;
            current.Append(ch);
        }

        if (quote is not null)
        {
            throw new CodingPolicyException("Unterminated quote in command.");
        }

        if (inToken)
        {
            tokens.Add(new CommandToken(current.ToString(), quoted));
        }

        return tokens;
    }

    private void CheckGit(List<string> args)
    {
        var sub = args.FirstOrDefault(a => a != "--no-pager");
        if (sub is null)
        {
            throw new CodingPolicyException("git needs a sub-command.");
        }

        if (args.Any(IsForbiddenGitGlobalOption))
        {
            throw new CodingPolicyException("git global options that change configuration or location are not allowed.");
        }

        if (!_allowedGitSubcommands.Contains(sub))
        {
            throw new CodingPolicyException($"git {sub} is not allowed here; branching, commit and push are handled for you. Allowed: {string.Join(", ", _allowedGitSubcommands.Order(StringComparer.OrdinalIgnoreCase))}.");
        }
    }

    private static bool IsForbiddenGitGlobalOption(string a)
        => a is "-c" or "-C" or "--exec-path" or "--git-dir" or "--work-tree" or "--config-env"
           || a.StartsWith("--exec-path=", StringComparison.Ordinal)
           || a.StartsWith("--git-dir=", StringComparison.Ordinal)
           || a.StartsWith("--work-tree=", StringComparison.Ordinal)
           || a.StartsWith("--config-env=", StringComparison.Ordinal);

    private static string Normalise(string exe)
    {
        var name = exe.Trim();
        foreach (var suffix in ExecutableSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return name;
    }
}
