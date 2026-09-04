namespace Agent.Host;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>When false the process runs as a plain worker: no Kestrel, no endpoints.</summary>
    public bool Enabled { get; set; } = true;

    public string ListenUrl { get; set; } = "http://localhost:5080";

    /// <summary>
    /// Development / test only: validate bearer tokens with this symmetric key instead of Keycloak's discovery
    /// document. Tokens must carry the configured issuer and audience.
    /// </summary>
    public string? DevSigningKey { get; set; }

    /// <summary>How many chat messages per CLI conversation are kept in memory.</summary>
    public int ConversationHistory { get; set; } = 40;
}
