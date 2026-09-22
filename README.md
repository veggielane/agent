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

### Choosing the coding engine

`Coding:Engine` selects which loop does the work. Everything around it, the clone, branch, verification,
commit, push and merge request, is the same either way.

| Engine | What it is |
|---|---|
| `Native` (default) | The built-in loop: our own file, search, edit and run tools, with turn, token, time and run-count budgets enforced by us. |
| `OpenCode` | Hands the task to the [opencode](https://opencode.ai) CLI running inside the sandbox. |

```jsonc
"Coding": {
  "Engine": "OpenCode",
  "Sandbox": { "Mode": "Docker" },        // strongly recommended with an external agent
  "OpenCode": { "ExecutablePath": "opencode", "Agent": "", "AllowWebAccess": false }
}
```

The agent generates opencode's config per run, so your policy survives the handover: allowed executables
become `permission.bash` rules, protected paths become `permission.edit` denials and are re-checked against
`git status` afterwards, committing and pushing are denied, and interactive questions are refused so a
headless run cannot hang. Verification still runs on our side and failures are fed back with
`opencode run --continue`.

Three things to know before switching. Only the wall-clock budget is enforceable, because opencode owns the
loop, and token counts become best-effort. The bash rules are enforced by opencode rather than by us, so the
container is the real boundary and `Process` mode logs a warning on every run. And the LLM API key must enter
the sandbox for opencode to call the model. The binary has to be present where commands run: install it on
the worker for `Process` mode, or bake it into the image for `Docker`.

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

**Maximum hardening.** Capabilities are dropped and no-new-privileges is set by default. Three further
controls are opt-in because each one breaks common toolchains unless you provide for it:

```jsonc
"Sandbox": {
  "User": "1000:1000",              // non-root; some images need root to install packages
  "ReadOnlyRootFilesystem": true,   // needs package caches mounted, since they live outside /work
  "Network": "none"                 // needs a pre-populated cache volume, or restores fail
}
```

Turn them on together with a profile that mounts the caches your builds need, and test one repository
before making it the default.

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

## Observability

Traces, metrics, and logs are emitted with OpenTelemetry. Point it at a collector:

```jsonc
"Telemetry": {
  "OtlpEndpoint": "http://otel-collector:4317",   // or set OTEL_EXPORTER_OTLP_ENDPOINT
  "OtlpProtocol": "grpc",                          // or httpprotobuf for port 4318
  "ConsoleExporter": false                         // true prints everything locally
}
```

With no endpoint the agent still records everything internally and ships nothing, so the instrumentation
is safe to leave enabled. A question produces one `agent.event` span with the command, model call, and tool
calls nested under it, alongside spans for outbound HTTP and SQL.

The metrics worth alerting on:

| Instrument | Watch for |
|---|---|
| `agent.polls` | **Silence.** A healthy poll that finds nothing still increments, so no data means a dead poller. |
| `agent.queue.depth` | A backlog that does not drain. |
| `agent.events` by `outcome` | A rising `error` share. |
| `agent.tokens` by `channel` and `purpose` | Cost attribution and runaway spend. |
| `agent.tasks` by `stop_reason` | Budget exhaustion, or verification failing repeatedly. |
| `agent.sandbox.commands` by `outcome` | Timeouts, which usually mean a wedged container. |

Spans carry identifiers, roles, counts, and outcomes, never message text or file contents. Prompts and
completions are recorded only if you set `Llm:EnableSensitiveTelemetry`.

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
| Anywhere | `!fix <issue-or-repo> <what to do>` starts a coding task from any channel, so work can be asked for in a Mattermost thread as well as from a ticket |
| Anywhere | `!help`, `!status`, `!tasks`, `!task 12` (`--actions` lists what the coding loop wrote and ran), `!cancel 12`, `!model`, `!whoami`, plus YAML commands like `!summarize` |

### How a coding task runs

1. It posts what it understood and how it plans to proceed, before writing anything. Cancel with
   `!cancel <id>` while the branch is still empty.
2. It works on a branch, runs your build and tests, and fixes what it broke.
3. It pushes and opens a merge request with the requester as reviewer, draft if verification failed.
   **The merge request is the approval gate.** The agent never merges and never pushes to a protected branch.
4. If the merge request's pipeline fails, it reads the failing jobs, tries to fix them on the same branch,
   and after a bounded number of attempts says so on the merge request and leaves it to a human.
5. Mentioning the agent on its own merge request queues a follow-up on that branch.

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
