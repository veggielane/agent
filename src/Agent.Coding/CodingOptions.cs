using Agent.Coding.OpenCode;
using Agent.Coding.Sandbox;

namespace Agent.Coding;

/// <summary>
/// Bound from the <c>Coding</c> section. Read through <c>IOptionsMonitor</c> so budget and policy edits
/// apply to the next task without a restart.
/// </summary>
public sealed class CodingOptions
{
    public const string SectionName = "Coding";

    public static readonly string[] DefaultAllowedExecutables =
        ["git", "dotnet", "node", "npm", "npx", "make", "python", "python3", "pytest", "cargo", "go"];

    public static readonly string[] DefaultProtectedPaths =
        [".gitlab-ci.yml", ".github/**", "**/*.pfx", "**/*.pem", "**/appsettings.Production.json", "deploy/**", ".agent/**", ".engex.yml", ".engex.yaml"];

    /// <summary>Git sub-commands the model may run through the <c>run</c> tool. Branching, commit and push belong to the orchestrator.</summary>
    public static readonly string[] DefaultAllowedGitSubcommands =
        ["status", "diff", "log", "show", "blame", "grep", "ls-files", "ls-tree", "rev-parse", "describe", "shortlog", "cat-file", "add", "restore", "rm", "mv", "version", "--version", "help", "--help"];

    public string WorkspaceRoot { get; set; } = DefaultWorkspaceRoot;

    public static string DefaultWorkspaceRoot { get; } = Path.Combine(Path.GetTempPath(), "agent-work");

    /// <summary>The configured root, or the default when the setting is blank.</summary>
    public string EffectiveWorkspaceRoot => string.IsNullOrWhiteSpace(WorkspaceRoot) ? DefaultWorkspaceRoot : WorkspaceRoot;

    public int MaxConcurrentTasks { get; set; } = 2;

    /// <summary>Which agent loop does the work: the built-in one, or the opencode CLI.</summary>
    public CodingEngineKind Engine { get; set; } = CodingEngineKind.Native;

    public OpenCodeOptions OpenCode { get; set; } = new();

    public CodingBudgetOptions Budget { get; set; } = new();

    /// <summary>Where the model's commands run: on the host, or in one container per task.</summary>
    public SandboxOptions Sandbox { get; set; } = new();

    /// <summary>Executable base names (without .exe/.cmd) the <c>run</c> tool may start. Configuring the key replaces the defaults.</summary>
    public List<string> AllowedExecutables { get; set; } = [.. DefaultAllowedExecutables];

    /// <summary>Glob patterns (relative to the repo root) the model may read but never write. Configuring the key replaces the defaults.</summary>
    public List<string> ProtectedPaths { get; set; } = [.. DefaultProtectedPaths];

    /// <summary>Git sub-commands allowed through <c>run</c>. Configuring the key replaces the defaults.</summary>
    public List<string> AllowedGitSubcommands { get; set; } = [.. DefaultAllowedGitSubcommands];

    /// <summary>
    /// Posts an understanding-and-plan note to the ticket or thread before any code is written, so a person
    /// can cancel before a branch exists rather than review a surprise afterwards.
    /// </summary>
    public bool PostPlan { get; set; } = true;

    /// <summary>Output cap for the plan. It is a paragraph and some bullets, not a design document.</summary>
    public int PlanMaxTokens { get; set; } = 600;

    /// <summary>Writes commit subjects as Conventional Commits (<c>fix:</c>, <c>feat:</c>, ...).</summary>
    public bool ConventionalCommits { get; set; } = true;

    public string BranchPrefix { get; set; } = "agent/";

    public bool OpenAsDraft { get; set; }

    public int KeepFailedWorkspacesDays { get; set; } = 3;

    public string CommitAuthorName { get; set; } = "Team Agent";

    public string CommitAuthorEmail { get; set; } = "agent@localhost";

    /// <summary>Tool results (process output, diffs, listings) are cut to this many characters, keeping head and tail.</summary>
    public int MaxToolOutputChars { get; set; } = 12_000;

    public int MaxFileReadChars { get; set; } = 40_000;

    public int RunTimeoutSeconds { get; set; } = 300;

    /// <summary>0 = full history; &gt;0 adds <c>--depth</c> (implies single-branch).</summary>
    public int CloneDepth { get; set; }

    /// <summary>Adds <c>--filter=blob:none</c> so blobs are fetched lazily.</summary>
    public bool UseBlobFilter { get; set; } = true;

    /// <summary>Upper bound on output tokens per model call in the coding loop (whole files are written through tools).</summary>
    public int MaxOutputTokens { get; set; } = 16_000;
}

public sealed class CodingBudgetOptions
{
    /// <summary>One turn = one model round trip including the tool calls it triggers.</summary>
    public int MaxTurns { get; set; } = 60;

    public long MaxTokens { get; set; } = 400_000;

    public int MaxMinutes { get; set; } = 30;

    /// <summary>Maximum <c>run</c> tool invocations per coding run.</summary>
    public int MaxRuns { get; set; } = 25;

    /// <summary>Extra model turns granted to fix a failing build/test before the MR is opened as draft.</summary>
    public int MaxVerifyRetries { get; set; } = 2;
}
