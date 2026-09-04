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
