using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Core.Llm;

namespace Agent.Coding.OpenCode;

/// <summary>
/// Builds the <c>opencode.json</c> the CLI runs under. The point of generating it rather than letting the
/// repository supply one is that the agent's own policy survives the handover: allowed executables and
/// protected paths become opencode permission rules, so the external agent is held to the same limits.
/// </summary>
public static class OpenCodeConfigWriter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Commands that stay the orchestrator's job no matter what the allow list says.</summary>
    public static readonly string[] DeniedGitCommands = ["git commit*", "git push*", "git reset*", "git checkout*", "git switch*", "git rebase*", "git config*"];

    public static string Build(OpenCodeOptions openCode, CodingOptions coding, LlmOptions llm, RepoProfile profile, string model)
    {
        var root = new JsonObject
        {
            ["$schema"] = "https://opencode.ai/config.json",
        };

        var providerOptions = new JsonObject { ["baseURL"] = llm.BaseUrl };
        if (!string.IsNullOrWhiteSpace(openCode.ApiKeyEnvironmentVariable))
        {
            providerOptions["apiKey"] = $"{{env:{openCode.ApiKeyEnvironmentVariable}}}";
        }

        var modelEntry = new JsonObject { ["name"] = model };
        if (openCode.ContextLimit > 0 || openCode.OutputLimit > 0)
        {
            var limit = new JsonObject();
            if (openCode.ContextLimit > 0)
            {
                limit["context"] = openCode.ContextLimit;
            }

            if (openCode.OutputLimit > 0)
            {
                limit["output"] = openCode.OutputLimit;
            }

            modelEntry["limit"] = limit;
        }

        root["provider"] = new JsonObject
        {
            [openCode.ProviderId] = new JsonObject
            {
                ["npm"] = "@ai-sdk/openai-compatible",
                ["name"] = openCode.ProviderName,
                ["options"] = providerOptions,
                ["models"] = new JsonObject { [model] = modelEntry },
            },
        };

        root["model"] = $"{openCode.ProviderId}/{model}";
        root["instructions"] = new JsonArray(RepoConfigLoader.InstructionsFile);

        if (openCode.ApplyPermissions)
        {
            root["permission"] = BuildPermissions(openCode, coding, profile);
        }

        return root.ToJsonString(Json);
    }

    /// <summary>
    /// Later rules win, so each block starts with a catch-all and narrows. Bash is deny-by-default and
    /// opens only what the agent's own allow list already permits.
    /// </summary>
    public static JsonObject BuildPermissions(OpenCodeOptions openCode, CodingOptions coding, RepoProfile profile)
    {
        var bash = new JsonObject { ["*"] = "deny" };
        foreach (var executable in coding.AllowedExecutables.Concat(profile.ExtraAllowedExecutables).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(executable))
            {
                bash[executable.Trim() + "*"] = "allow";
            }
        }

        // Branching, committing and pushing belong to the orchestrator; these come last so they win.
        foreach (var denied in DeniedGitCommands)
        {
            bash[denied] = "deny";
        }

        var edit = new JsonObject { ["*"] = "allow" };
        foreach (var pattern in coding.ProtectedPaths.Concat(profile.ExtraProtectedPaths).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                edit[pattern.Trim()] = "deny";
            }
        }

        var web = openCode.AllowWebAccess ? "allow" : "deny";
        return new JsonObject
        {
            ["read"] = "allow",
            ["glob"] = "allow",
            ["grep"] = "allow",
            ["lsp"] = "allow",
            ["task"] = "allow",
            ["edit"] = edit,
            ["bash"] = bash,
            ["webfetch"] = web,
            ["websearch"] = web,
            ["external_directory"] = "deny",

            // The run is non-interactive: a question would hang until the timeout.
            ["question"] = "deny",
        };
    }
}
