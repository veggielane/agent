namespace Agent.Coding.OpenCode;

/// <summary>
/// Bound from <c>Coding:OpenCode</c>. Only used when <c>Coding:Engine</c> is <c>OpenCode</c>, where the
/// agent hands the work to the opencode CLI instead of running its own loop.
/// </summary>
public sealed class OpenCodeOptions
{
    /// <summary>Executable name or absolute path, resolved inside the sandbox.</summary>
    public string ExecutablePath { get; set; } = "opencode";

    /// <summary>Provider id used in the generated config and in <c>--model provider/model</c>.</summary>
    public string ProviderId { get; set; } = "team";

    public string ProviderName { get; set; } = "Team LLM";

    /// <summary>Overrides the model. Empty uses <c>Llm:CodingModel</c> under <see cref="ProviderId"/>.</summary>
    public string? Model { get; set; }

    /// <summary>Passed as <c>--agent</c> when set.</summary>
    public string? Agent { get; set; }

    /// <summary>Extra arguments appended to <c>opencode run</c>.</summary>
    public List<string> ExtraArgs { get; set; } = [];

    /// <summary>
    /// Translates the agent's own policy (allowed executables, protected paths) into opencode's permission
    /// block. Turning this off hands opencode an unrestricted tool surface, leaving the container as the
    /// only control.
    /// </summary>
    public bool ApplyPermissions { get; set; } = true;

    /// <summary>Lets opencode reach the web. Off by default; the sandbox network setting still applies.</summary>
    public bool AllowWebAccess { get; set; }

    /// <summary>Environment variable the generated config reads the API key from.</summary>
    public string ApiKeyEnvironmentVariable { get; set; } = "OPENCODE_LLM_API_KEY";

    /// <summary>Config file written to the repository root and excluded from git for the run.</summary>
    public string ConfigFileName { get; set; } = "opencode.agent.json";

    /// <summary>Context window advertised to opencode for the model. 0 omits the limit.</summary>
    public int ContextLimit { get; set; }

    /// <summary>Maximum output tokens advertised to opencode. 0 omits the limit.</summary>
    public int OutputLimit { get; set; }

    /// <summary>Seconds allowed for the <c>opencode --version</c> preflight.</summary>
    public int PreflightTimeoutSeconds { get; set; } = 60;
}
