namespace Agent.Infrastructure.Ldap;

/// <summary>One directory entry: its DN and the requested attributes (multi-valued).</summary>
public sealed record LdapEntry(string Dn, IReadOnlyDictionary<string, string[]> Attributes)
{
    public string? First(string attribute)
        => Attributes.TryGetValue(attribute, out var values) && values.Length > 0 ? values[0] : null;

    public string[] All(string attribute)
        => Attributes.TryGetValue(attribute, out var values) ? values : [];
}

/// <summary>Thin abstraction over an LDAP subtree search so the provider can be tested without a directory.</summary>
public interface ILdapSearcher
{
    Task<IReadOnlyList<LdapEntry>> SearchAsync(string baseDn, string filter, string[] attributes, CancellationToken cancellationToken);
}
