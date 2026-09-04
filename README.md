# Team Agent

A .NET 10 agent for the team. It answers questions and carries out coding tasks for people in
**Mattermost**, **Jira Data Center**, **GitLab**, and a **CLI**, backed by any OpenAI-compatible LLM
endpoint. Access is limited to members of configured AD groups, resolved through Keycloak, with three roles:
`users` (ask), `team` (give work), `admin` (operate).

See [`plan.md`](plan.md) for the design and [`CLAUDE.md`](CLAUDE.md) for a code map.

## Build and test

```bash
dotnet build agent.slnx
dotnet test agent.slnx
```

Tests never touch a real system: HTTP is mocked with WireMock.Net, the LLM with a scripted client, the
database with EF Core on SQLite.

## Run the host

```bash
cd src/Agent.Host
dotnet user-secrets set "Llm:ApiKey" "…"
dotnet user-secrets set "Mattermost:BotToken" "…"
dotnet user-secrets set "Jira:Token" "…"
dotnet user-secrets set "GitLab:Token" "…"
dotnet user-secrets set "Keycloak:Admin:ClientSecret" "…"
dotnet user-secrets set "Persistence:ConnectionString" "Server=sql01;Database=Agent;Integrated Security=true;Encrypt=true"
dotnet run
```

`appsettings.json` documents every setting. A channel starts only when its `BaseUrl` is set. Without a
`Persistence:ConnectionString` the host keeps tasks and cursors in memory (fine for trying things out).
`Api:Enabled=false` runs the process as a pure worker with no HTTP listener.

### Running coding tasks in a container

By default the model's commands run as child processes on the worker host. To isolate each task in its own
container instead, set `Coding:Sandbox:Mode` to `Docker` (needs a reachable Docker daemon — Linux, Docker
Desktop, or WSL2):

```jsonc
"Coding": {
  "Sandbox": {
    "Mode": "Docker",
    "Network": "none",                       // or a network restricted to GitLab + your registries
    "Images": { "dotnet": "our-registry/dotnet-sdk:10.0" },
    "Volumes": ["agent-nuget:/root/.nuget/packages"],   // pre-populated cache, needed when Network is "none"
    "Memory": "4g", "Cpus": 2, "User": "1000:1000"
  }
}
```

The repository is bind-mounted at `/work` and the container is removed when the task ends. Git, credentials,
and file edits stay on the host, so **the container never sees a token**. `!status` reports the daemon version
or why it is unavailable.

**Per-repository containers.** A repository asks for what it needs in `.engex.yml` at its root
(see [`docs/repo-config-examples/.engex.yml`](docs/repo-config-examples/.engex.yml)):

```yaml
container:
  profile: node-chromium    # a name from the host's menu — the safe way
  memory: 6g                # honoured downward, clamped at MaxMemory
  network: none             # a repo may close the network, never open it
```

The repository requests; the host decides. `.engex.yml` is repository content, and anyone who can open a
merge request can edit it, so an image is treated as arbitrary code:

| Repo writes | Granted when |
|---|---|
| `profile: <name>` | The name exists in `Coding:Sandbox:Profiles`. Unknown names fail the task and list what is available. |
| `image: <ref>` | It matches a glob in `Coding:Sandbox:AllowedImages`. That list is **empty by default**, so raw images are refused until you opt in. |
| `memory` / `cpus` | Always, clamped to `MaxMemory` / `MaxCpus`. |
| `network` | Only `none`, or the value the host already uses. |
| `env` | Always, but host values win on a collision. |
| mounts | Never. Put cache volumes on a profile instead. |

With nothing in `.engex.yml`, the image comes from the repository's build command (dotnet, node, python, go,
rust) and then `DefaultImage`. The agent cannot edit `.engex.yml`: it is protected, so a task cannot rewrite
the policy it runs under.

**Three inputs, kept separate.** The task comes from the request, the ticket or mention that started it, and
only from there. `.engex.yml` holds structured settings: build and test commands, container, protected paths.
`AGENTS.md` holds standing repository conventions, the things that would otherwise be rediscovered on every
task. Neither repository file can tell the agent what work to do.

Prerequisites on the worker: Linux containers, and **`Coding:WorkspaceRoot` must be a directory the Docker
daemon is allowed to bind-mount**. On Docker Desktop that means adding it under Settings → Resources → File
sharing (or using the WSL2 backend with a path inside WSL); an unshared path makes `docker run` hang rather
than fail, which surfaces as a task stuck in Preparing until the start timeout. Verify with:

```bash
docker run --rm -v "<your workspace root>:/work" -w /work alpine ls /work
```

Runtime files next to the binary:

| Path | Purpose |
|------|---------|
| `prompts/system.md`, `prompts/coding.md` | System prompts (per-channel override: `system.jira.md`) |
| `commands/*.yaml` | Prompt-template `!commands`, hot-reloaded |
| `mcp/*.json` | MCP server definitions, hot-reloaded |

## Use the CLI

```bash
dotnet run --project src/Agent.Cli -- login                 # Keycloak device flow
dotnet run --project src/Agent.Cli -- ask "what is open on PROJ-12?"
dotnet run --project src/Agent.Cli -- chat                  # REPL; !commands work here too
dotnet run --project src/Agent.Cli -- cmd status
dotnet run --project src/Agent.Cli -- task create --repo https://gitlab.internal/team/repo.git "add retries to the http client"
dotnet run --project src/Agent.Cli -- task log 1 --follow
dotnet run --project src/Agent.Cli -- ask "…" --local        # run the core in-process (needs Llm settings)
```

The CLI reads `appsettings.json` next to the binary, then `%APPDATA%/agent/appsettings.json`, then
`AGENT_*` environment variables (e.g. `AGENT_Agent__Server`).

## Talking to the agent

| Where | How |
|-------|-----|
| Mattermost | DM the bot, or `@agent` in a channel (replies go in a thread; the bot follows that thread afterwards) |
| Jira | `[~agent-bot]` in a comment to ask; add the `agent` label to hand the issue over as a coding task |
| GitLab | `@agent-bot` in an issue or MR note; assign an issue to the bot or label it `agent` to start a task; mention it on the agent's MR for follow-ups |
| Anywhere | `!help`, `!status`, `!tasks`, `!task 12`, `!cancel 12`, `!model`, `!whoami`, plus YAML commands like `!summarize` and `!fix` |

## Layout

```
src/Agent.Core            core: pipeline, authorization, commands, tools, tasks, LLM factory
src/Agent.Coding          coding engine: workspace, git, tools, budgets
src/Agent.Worker          task worker: clone → work → verify → commit → push → MR
src/Agent.Mcp             MCP client integration
src/Agent.Persistence     EF Core (SQL Server; SQLite for dev/tests)
src/Agent.Infrastructure.Keycloak / .Ldap   group membership, device flow, token cache
src/Agent.Channels.*      Mattermost (WebSocket), Jira (polling), GitLab (polling)
src/Agent.Host            the service (+ optional API)
src/Agent.Cli             the CLI
tests/                    one test project per component
```
