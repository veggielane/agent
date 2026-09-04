using System.Text;

namespace Agent.Infrastructure.Ldap;

public static class LdapFilter
{
    /// <summary>Escapes an assertion value for use inside a search filter (RFC 4515 §3).</summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        StringBuilder? sb = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var escaped = c switch
            {
                '\\' => "\\5c",
                '*' => "\\2a",
                '(' => "\\28",
                ')' => "\\29",
                '\0' => "\\00",
                _ => null,
            };

            if (escaped is null)
            {
                sb?.Append(c);
                continue;
            }

            sb ??= new StringBuilder(value.Length + 8).Append(value, 0, i);
            sb.Append(escaped);
        }

        return sb?.ToString() ?? value;
    }
}
