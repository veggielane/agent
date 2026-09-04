using System.Text;

namespace Agent.Core.Commands;

public sealed record CommandToken(string Value, int Start, int End, bool Quoted)
{
    public bool IsLongOption => !Quoted && Value.StartsWith("--", StringComparison.Ordinal) && Value.Length > 2;

    public bool IsShortOption => !Quoted && Value.Length == 2 && Value[0] == '-' && char.IsLetter(Value[1]);
}

/// <summary>Splits "a \"b c\" --x=1 --flag" into tokens, remembering where each token started in the source text.</summary>
public static class CommandTokenizer
{
    public static IReadOnlyList<CommandToken> Tokenize(string text)
    {
        var tokens = new List<CommandToken>();
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }

        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            var start = i;
            var sb = new StringBuilder();
            var quoted = false;
            char? quote = null;

            while (i < text.Length)
            {
                var c = text[i];
                if (quote is null && char.IsWhiteSpace(c))
                {
                    break;
                }

                if (quote is null && (c == '"' || c == '\''))
                {
                    quote = c;
                    quoted = true;
                    i++;
                    continue;
                }

                if (quote is not null && c == quote)
                {
                    quote = null;
                    i++;
                    continue;
                }

                if (c == '\\' && i + 1 < text.Length && (text[i + 1] == '"' || text[i + 1] == '\'' || text[i + 1] == '\\'))
                {
                    sb.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            tokens.Add(new CommandToken(sb.ToString(), start, i, quoted));
        }

        return tokens;
    }
}
