# Team Agent — Plan

A .NET agent for the team. It answers questions in Mattermost, Jira, GitLab, and a CLI, and it
carries out coding tasks: a ticket in Jira or GitLab (or a mention on a merge request) turns into a
branch, commits, and a merge request for human review. Backed by an OpenAI-compatible LLM endpoint.
Only members of configured Active Directory groups can talk to it or give it work.

Status: v3 — **implemented** (2026-09-04). Sections marked **[decide]** still describe choices the team can
revisit; the code follows the recommendation in each case. See "Implementation status" at the end.

Changes from v1: added GitLab as a channel, the coding-task pipeline (sections 6.7–6.10), persistence,
sandboxing, a much stronger security section, and reordered milestones so the coding path lands early.

### Decisions so far (2026-09-04)

| Topic | Decision |
|-------|----------|
| Jira | Data Center. Bot account available. **Webhooks cannot be used** → the agent polls Jira (6.4). No Jira inbound endpoint exists. |
| GitLab | **Webhooks cannot be used** → the agent polls GitLab (bot to-do list + labelled issues, 6.5). |
| Mattermost | Bot account available. No outgoing webhooks / slash commands → WebSocket only (6.3). |
| Identity | On-prem Active Directory behind **Keycloak (OIDC)**. The CLI logs in with Keycloak, the API validates Keycloak tokens, and roles come from Keycloak groups (AD via LDAP federation); direct LDAP stays as a fallback provider (6.2). |
| ASP.NET Core | Used only for the optional CLI/MCP API and health endpoints; the channels and worker run on a plain Generic Host, and `Api.Enabled=false` removes Kestrel entirely (6.6). |
| LLM model selection | Bound through the Options pattern (`IOptionsMonitor<LlmOptions>`), hot-reloadable, separate models for answering and coding (6.1). |
| Authorization model | Three roles — **users**, **team**, **admin** — each mapped to AD groups. Every command, YAML command, MCP server, and tool declares the minimum role that may use it (6.2). |
| Persistence | **EF Core on SQL Server**, only for state that must survive restarts (tasks, cursors, idempotency, audit) (6.10). |
| Mattermost behaviour | Direct messages need no mention; channel mentions are answered in a thread; the bot follows threads it has replied in (6.3). |

Inbound transports are therefore: Mattermost = WebSocket, Jira = polling, GitLab = polling. Nothing
external calls into the agent; the only inbound HTTP surface is the optional CLI API (Keycloak
bearer tokens, intranet only) plus health checks. All three channels sit behind one `IEventSource` abstraction.

---

## 1. Goals

- **Answer** questions in Mattermost (DMs / @mentions), on Jira issues, on GitLab issues and MRs, and
  from a CLI.
- **Do coding work** when asked via a ticket:
  - A GitLab issue labelled/mentioned for the agent → the agent works in that project, pushes a branch,
    opens an MR, and comments back with the link.
  - A Jira issue labelled/mentioned for the agent → same, with the target repository resolved from the
    ticket (see 6.4).
  - A mention on an existing MR → the agent revises that MR's branch (review follow-ups).
- **Human review is the gate.** The agent never merges and never pushes to protected branches.
- **One authorization rule everywhere**: the requester must be in an allowed AD group. Applies to
  questions, to task creation, and to follow-up instructions.
- **`!commands`**: deterministic commands (`!status`, `!tasks`, `!cancel 42`, `!summarize`, …) that
  work identically in every channel and are trivial to add — one attributed method, or one YAML file
  for prompt-based commands (6.12).
- **MCP tools**: any Model Context Protocol server (stdio or HTTP) can be plugged in by configuration
  and its tools become available to the LLM, governed by the same roles, budgets, and audit as
  native tools (6.13).
- One codebase, one core, thin channel adapters; the LLM endpoint and model are configuration.

## 2. Non-goals (v1)

- Merging MRs, pushing to protected branches, closing or transitioning tickets on its own.
- Long-term memory across conversations (context comes from the thread / issue / MR).
- Multi-team or multi-tenant deployments; a web UI.
- Full container-per-task sandboxing on day one (planned for M9; v1 uses process isolation, see 6.9).

## 3. Assumptions **[decide]**

| # | Assumption | If wrong… |
|---|------------|-----------|
| A1 | .NET 10 (LTS). ".NET Core" today means modern .NET. | Pin a different SDK in `global.json`. |
| A2 | **Confirmed:** on-prem Active Directory, reached through Keycloak (A9). LDAPS and a bind account are only needed if the direct-LDAP fallback provider is used. | Keycloak exposes the groups and LDAP is never touched by the agent. |
| A3 | Mattermost, Jira, and GitLab logins go through Keycloak or AD/LDAP, so usernames/emails map to one identity. | Explicit user-mapping table. |
| A4 | **Confirmed:** Jira Data Center with a bot account and **no webhooks**. Assumed: the bot can run JQL across the in-scope projects and comment on issues. | If the bot cannot see a project, that project is out of scope. |
| A5 | **Confirmed:** GitLab webhooks cannot be used → polling. Assumed: self-managed GitLab; a bot user with a PAT and Developer membership on the in-scope groups; the To-Do, Issues, MR, and Notes APIs are reachable with that token. | If the bot cannot be a member of a project, that project is out of scope. |
| A6 | The LLM endpoint supports chat completions **with tool calling** and has a model strong enough for multi-step coding (large context, good instruction following). | Without tool calling there is no coding agent; with a weak model the coding path will be unreliable. Verify in M1. |
| A7 | A worker machine (Linux VM preferred, Windows acceptable for v1) can hold the toolchains the repos need (`dotnet`, `node`, …) and later run Docker. | Toolchain-per-task containers become mandatory sooner. |
| A8 | The team's repos have build/test commands that can run headless. | Agent can still edit and open MRs but cannot self-verify. |
| A9 | **Confirmed:** Keycloak is the OIDC provider. Assumed: Keycloak federates AD via LDAP user federation and exposes AD group membership (group mapper in tokens, Admin API); we can register a public CLI client with the device flow and a confidential service client with `view-users`. | Roles resolved via direct LDAP instead (same interface); CLI uses auth-code + PKCE if the device flow is disabled. |

## 4. Architecture

Two paths share one core: a **fast path** (answer a question, reply in place) and a **task path**
(long-running coding work, tracked in a database, executed by a worker).

```
 Mattermost --ws-------+                                                       
 Jira <--poll----------+--> Agent.Host ----------------------------------------+
 GitLab <--poll--------+    | channel adapters: parse event -> resolve identity |
 CLI --HTTP(OIDC)------+    |                   -> authorize -> classify        |
                            |                                                  |
                            |   !command ----------> Command dispatcher        |
                            |                        -> reply via channel      |
                            |                                                  |
                            |   question ----------> Agent.Core (answer loop)  |
                            |                        -> reply via channel      |
                            |                                                  |
                            |   task request ------> Task store (DB)           |
                            |                        -> ack comment on ticket  |
                            +--------------------------------------------------+
                                             |
                                             v  (queue; same process in v1, separate service later)
                            +--------------------------------------------------+
                            | Agent.Worker                                     |
                            |  clone -> branch -> coding loop (LLM + tools)    |
                            |  -> build/test -> commit -> push -> open/update  |
                            |  MR -> final comment on ticket / MR              |
                            |                                                  |
                            |  workspace per task, allow-listed commands,      |
                            |  bot token never visible to the coding loop      |
                            +--------------------------------------------------+
```

Principles:

- **One core, thin adapters.** Adapters translate wire formats to `AgentRequest` / `TaskRequest` and
  post replies. Behaviour (authz, prompting, tools, task lifecycle) lives in `Agent.Core`.
- **Authorization before anything.** A request without an allowed `CallerIdentity` never reaches the
  LLM or the task queue. Follow-up instructions on a task are re-authorized individually.
- **The ticket is the source of truth.** Every task is anchored to a GitLab issue, Jira issue, or MR;
  all progress and results are posted there. Mattermost and the CLI can *create* tasks but the
  record lives in the ticketing system.
- **Human review is the only merge path.** The bot account has Developer rights, protected branches
  stay protected, MRs need approval as usual.
- **The CLI is a client of the host** (Keycloak OIDC bearer tokens) with an in-process `--local` mode for
  development.

### 4.1 Native coding loop vs. external coding CLI **[decide]**

| Option | Pros | Cons |
|--------|------|------|
| **A. Native loop in .NET** (recommended) | Full control of tools, budgets, audit, and sandboxing; no extra runtime; authz and secrets stay in our process. | We build and tune the edit/search/run tools and context management ourselves. |
| B. Orchestrate an external coding CLI (e.g. aider, opencode, Codex CLI) pointed at the OpenAI-compatible endpoint | Mature editing behaviour on day one. | Extra runtime + config drift; harder to enforce budgets/allow-lists; agent quality depends on a third-party tool's support for our endpoint. |

**Both are implemented**, selected by `Coding:Engine` (`Native` by default, `OpenCode` for the CLI). The
pipeline, workspace, git, verification and merge-request publishing are identical either way; only the loop
in the middle differs. 6.8.1 covers what the orchestrator can still guarantee when the loop belongs to
somebody else, and what it cannot.

### 4.2 Tech stack

| Layer | Choice | Notes |
|-------|--------|-------|
| Runtime | **.NET 10 (LTS)**, C# 14 | Generic Host worker for the service; ASP.NET Core (Kestrel + minimal APIs) only for the optional CLI/MCP API and health endpoints (6.6). |
| LLM | `Microsoft.Extensions.AI` + official `OpenAI` SDK | `IChatClient` against the OpenAI-compatible endpoint; function calling via `UseFunctionInvocation()`; models chosen through `IOptionsMonitor<LlmOptions>`. |
| MCP | `ModelContextProtocol` C# SDK | Client for stdio and HTTP servers; optional server side via `ModelContextProtocol.AspNetCore`. |
| Database | **SQL Server** via EF Core (`Microsoft.EntityFrameworkCore.SqlServer`) | Code-first migrations; Windows-auth connection from the service account; LocalDB or a SQL Server container for dev and tests. |
| Identity / auth | **Keycloak OIDC**: `Microsoft.AspNetCore.Authentication.JwtBearer` validates tokens on the API; the CLI uses the OAuth device flow (`Duende.IdentityModel` helpers or plain `HttpClient`). Roles from Keycloak groups via token claims (CLI) or the Keycloak Admin API (channel callers); `System.DirectoryServices.Protocols` as the direct-LDAP fallback provider. | Keycloak federates AD, so AD group membership still decides. |
| Mattermost | REST v4 + WebSocket (`ClientWebSocket`) | Small typed `HttpClient` wrapper; no third-party SDK. |
| Jira DC | REST v2, polled | Typed `HttpClient`; JQL watermarks. |
| GitLab | REST v4, polled (to-dos, issues, MRs, notes) | Typed `HttpClient`; `git` CLI over HTTPS for clone and push. |
| HTTP resilience | `Microsoft.Extensions.Http.Resilience` (Polly) | Retries, timeouts, circuit breakers on every outbound client. |
| Coding engine | Native loop on `IChatClient`; `CliWrap` for processes; `git` CLI; `Microsoft.Extensions.FileSystemGlobbing` | Per-task workspace; Docker per task from M9. |
| Commands | Own tokenizer/binder; `YamlDotNet` for YAML commands; `PhysicalFileProvider` watchers for hot reload | |
| Formatting | `Markdig` (markdown AST) → per-channel renderers: Mattermost markdown, Jira wiki markup, GitLab markdown | |
| CLI | `Spectre.Console` + `Spectre.Console.Cli`; SSE client for streaming | `agent login` (device flow); tokens cached per user (DPAPI on Windows, `0600` file on Linux). |
| Configuration | `appsettings.json` + env vars + `dotnet user-secrets`; Options pattern with validation | Secrets from env or a vault in production. |
| Observability | **OpenTelemetry** traces, metrics and logs over OTLP (6.14); `Microsoft.Extensions.Logging`, health checks | Core emits through `ActivitySource`/`Meter` only; the host owns the SDK. Audit trail in SQL Server. |
| Testing | xunit, NSubstitute, WireMock.Net, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.MsSql` | |
| Build / CI / deploy | `dotnet` SDK pinned by `global.json`, central package management, GitLab CI; Windows service (`UseWindowsService`) or Linux systemd / container | |

## 5. Solution layout

```
agent.sln
global.json / Directory.Build.props / Directory.Packages.props
src/
  Agent.Core/                    # domain, no infra deps
    Agent/                       #   IAgent (answer loop), AgentRequest/Response
    Authorization/               #   IAuthorizationService, CallerIdentity, IGroupMembershipProvider
    Events/                      #   IEventSource (WebSocket / polling), InboundEvent, cursors
    Commands/                    #   CommandRegistry, tokenizer/binder, dispatcher, built-ins, YAML loader
    Tasks/                       #   Task aggregate + state machine, TaskRequest, ITaskStore, ITaskQueue
    Llm/                         #   IChatClient factory, prompt assembly, tool registry
    Formatting/                  #   markdown -> per-channel
  Agent.Coding/                  # ICodingEngine: workspace, coding tools, loop, budgets, repo config
  Agent.Mcp/                     # MCP client provider, governed-tool decorator, optional MCP server
  Agent.Infrastructure.Keycloak/ # Keycloak Admin API group provider + OIDC helpers shared with the CLI
  Agent.Infrastructure.Ldap/     # direct LDAP group provider (fallback)
  Agent.Persistence/             # EF Core on SQL Server: tasks, events, cursors, audit, migrations
  Agent.Channels.Mattermost/
  Agent.Channels.Jira/
  Agent.Channels.GitLab/         # poller (to-dos, issues, MR state), REST client, git push credentials
  Agent.Host/                    # Generic Host: listeners, pollers, DI, worker (v1); optional Kestrel API
    commands/                    #   prompt-template commands (*.yaml), hot-reloaded at runtime
    mcp/                         #   drop-in MCP server definitions (*.json), hot-reloaded
    prompts/                     #   system prompts
  Agent.Worker/                  # hosted service running tasks; own executable when split out
  Agent.Cli/
tests/
  Agent.Core.Tests/ Agent.Coding.Tests/ Agent.Channels.*.Tests/ Agent.Host.Tests/
```

## 6. Components

### 6.1 Agent.Core — answer loop

- `AgentRequest { Channel, CallerIdentity, ConversationId, Text, History }` → `AgentResponse`.
- `IChatClient` from `Microsoft.Extensions.AI` + the `OpenAI` SDK with a custom `Endpoint`;
  `UseFunctionInvocation()` runs tool calls.
- **Model selection is configuration**, not code:
  - `LlmOptions { BaseUrl, ApiKey, AnswerModel, CodingModel, Overrides, TimeoutSeconds, … }` bound
    from the `Llm` section and validated on start (`ValidateDataAnnotations` + `ValidateOnStart`).
  - Consumed through `IOptionsMonitor<LlmOptions>` so a change in `appsettings.json` (or a mounted
    config file) switches models without a restart; the `IChatClient` factory resolves the model per
    request from the current snapshot.
  - `Overrides` allows a model per purpose/channel, e.g. `{"Jira.Answer": "small-model"}`; unset
    entries fall back to `AnswerModel` / `CodingModel`.
  - The CLI exposes `--model` for one-off experiments; the server ignores client-supplied models unless
    `Llm.AllowClientModelOverride` is true.
- Read-only tools for Q&A: `jira_get_issue`, `jira_search`, `gitlab_get_issue`, `gitlab_get_mr`,
  `gitlab_search_code`, `gitlab_read_file`. A question about "why does X fail" can read code without
  starting a task.
- All tools live in one `IToolRegistry`: native tools, commands flagged `ExposeAsTool` (6.12), and
  MCP server tools (6.13). The tool list offered on each request is filtered by caller role, channel,
  and scope, so the model only ever sees tools the caller may use.
- Guardrails: token caps, timeout, per-user rate limit, tool-call cap per turn.

### 6.2 Authorization (AD groups via Keycloak)

- `CallerIdentity { Channel, ChannelUserId, Username?, Email?, Upn?, LdapDn?, Roles }`.
- **Identity provider: Keycloak (OIDC).** Keycloak federates AD through LDAP user federation, so AD
  groups are visible as Keycloak groups. A caller's groups reach the agent in one of three ways, all
  behind `IGroupMembershipProvider`:
  - **From the token** (CLI / API callers): the access token carries a `groups` claim (Keycloak
    group-membership mapper) or realm roles. No lookup needed.
  - **From the Keycloak Admin API** (Mattermost, Jira, GitLab callers never present a token): a
    confidential service client with `view-users` resolves username / email →
    `GET /admin/realms/{realm}/users?username=…` → `GET /users/{id}/groups`. Exact match when those
    systems also log in through Keycloak. Cached with TTL.
  - **Direct LDAP fallback** (`System.DirectoryServices.Protocols`, nested groups via
    `(memberOf:1.2.840.113556.1.4.1941:=<groupDN>)`) if Keycloak does not expose the groups.
    `Authorization.Provider` selects it; nothing else in the system changes.
- **Roles.** Three roles, each mapped to one or more group identifiers in `Authorization.Roles`
  (Keycloak group paths such as `/agent/team`, or AD DNs when the LDAP provider is used):
  - `Users` — may ask questions and run user-level commands.
  - `Team` — everything `Users` can, plus giving the agent coding work and follow-ups.
  - `Admin` — everything, plus operational commands (`!model set --global`, `!reload`, `!mcp reload`).

  Roles are hierarchical (`Admin` ⊇ `Team` ⊇ `Users`). A caller's roles are resolved once per request
  from AD group membership (cached) and carried on `CallerIdentity.Roles`; `!whoami` prints them.
- **Every command, YAML command, MCP server (with per-tool overrides), and native tool declares the
  minimum role that may use it.** The registry rejects a definition without a role. Plain questions
  require `Users`; task creation and follow-ups require `Team`. The check runs before argument
  parsing and before any LLM call. Commands may apply finer rules in code, e.g. `!cancel` on someone
  else's task requires `Admin`, via `ctx.Caller.HasRole(Role.Admin)`.
- Deny behaviour **[decide]**: silent, or a one-line refusal (probably silent in public channels,
  explicit on tickets).
- Every decision → audit log with channel, identity, matched group, action (ask / task / follow-up).

### 6.3 Mattermost channel

- **Confirmed transport:** bot account + token over WebSocket `/api/v4/websocket` (`posted`
  events). No outgoing webhooks or slash commands are used. Reconnect with backoff; on reconnect,
  re-fetch posts since the last seen `create_at` for the bot's DM channels and mentions to close gaps.
- **Direct messages** — first-class. Any post in a DM channel with the bot (channel type `D`) is
  addressed to it; no mention needed. The DM channel is one rolling conversation: context is the last
  `Mattermost.DmHistoryMessages` posts (default 30) in that channel, or the thread if the user replies
  inside a thread in the DM. Group messages (type `G`) require a mention **[decide]**.
- **Channel mentions** — `@bot` in a public or private channel. The reply always goes **into a
  thread** rooted at the mentioned post (`root_id` = the post's own `root_id` if it is already a
  reply, else its id), so channels stay tidy and the thread is a stable `ConversationId`.
- **Thread continuation** — once the bot has replied in a thread, later posts in that thread by
  authorized users count as follow-ups without re-mentioning, for `Mattermost.ThreadFollowMinutes`
  (default 120) after the bot's last reply **[decide]**. Context is the full thread via
  `GET /posts/{id}/thread`, oldest posts trimmed to the token budget. Posts from unauthorized users in
  the thread are context only, never instructions.
- **Task threads** — when a task is requested from chat, its thread receives the acknowledgement, the
  MR link, and the final summary, in addition to the ticket.
- **Feedback while working** — 👀 reaction on the triggering post immediately; `user_typing` over the
  WebSocket while generating; on completion the reaction becomes ✅ (or ❌ with a short error).
  Optional streaming by editing the reply post every ~1 s **[decide]** — nice in DMs, noisy in busy
  channels.
- **Long replies** split at the server's max post length (default 16383 chars) into consecutive
  posts in the same thread, never inside a code fence.
- Ignore: the bot's own posts, other bots, system posts (`type != ""`), edits and deletions.
  Optional channel allow-list. Denied users get silence in channels and a one-line refusal in DMs.
- Identity: `GET /users/{id}` → username/email → AD roles.
- Task creation from chat (optional, later): "@bot in group/repo: add …" → agent opens a GitLab issue
  on the requester's behalf and proceeds as a normal task, replying with the issue + MR links.

### 6.4 Jira channel

- **Confirmed transport: polling** (Jira DC, no webhooks). A `JiraPoller` hosted service runs every
  `Jira.PollSeconds` (default 30):
  - JQL per in-scope project: `project = X AND updated >= "<watermark>" ORDER BY updated ASC`, with
    `fields=summary,description,labels,status,comment,updated` and paging. The watermark is the
    max `updated` seen minus a small overlap, stored per project in `ChannelCursors` (6.10).
  - For each returned issue, diff against stored state: new comments (by comment id) containing
    `[~botuser]` → question / follow-up; label `agent` newly present and no task yet → coding task.
    Processed comment ids and the label state are persisted, so restarts and the overlap window never
    double-process.
  - Cost: one search call per project per interval plus detail fetches only for changed issues. With
    a handful of projects this is negligible for Jira DC.
  - Latency: worst case one poll interval; acceptable for tickets. Mattermost stays real-time.
- Poller robustness: the JQL `updated` field has minute granularity on DC, so the overlap window is
  at least two minutes and dedupe is by comment id / label state, never by timestamp alone. Jira
  outages or 429s back off exponentially and never advance the watermark. Comments edited after
  first processing are ignored (no re-trigger).
- Triggers **[decide]**: label `agent` added, or `[~botuser]` mention. Mention in a comment = question
  or follow-up; label = coding task.
- Repo resolution for tasks **[decide]**, in order: a GitLab URL in the issue text; a custom field
  `Repository`; component → repo map in config; Jira project → default repo in config. Unresolvable →
  comment asking for the repo, task stays `NeedsInput`.
- Replies via REST v2 `POST /issue/{key}/comment` (wiki markup; works on DC and Cloud).
- Branch names include the Jira key (`agent/PROJ-123-short-slug`) so GitLab's Jira integration links
  the MR in the Jira dev panel; the agent also comments the MR link.
- Identity: comment/issue author → `name` / `emailAddress` (DC).

### 6.5 GitLab channel

- Bot user with a PAT (or group access token), **Developer** role on the in-scope groups. Membership
  matters twice: it lets the bot push branches, and it makes GitLab create **to-dos** for the bot
  whenever someone mentions or assigns it.
- **Confirmed transport: polling** (no webhooks). A `GitLabPoller` hosted service runs every
  `GitLab.PollSeconds` (default 30) and makes three kinds of calls:
  1. **Mentions and assignments** via the bot's to-do list: `GET /todos?state=pending`. GitLab creates
     a to-do when the bot is `mentioned` / `directly_addressed` in an issue or MR note (including diff
     discussions) or is `assigned` an issue/MR. Each to-do carries project, target (issue/MR), author,
     note body, and a `target_url` whose `#note_<id>` anchor identifies the note (fallback: match by
     author + body via the Notes API). After the event is persisted the poller calls
     `POST /todos/:id/mark_as_done`, which doubles as the acknowledgement and keeps processing
     idempotent across restarts. One request covers every project the bot is a member of.
  2. **Labelled issues**: `GET /groups/:id/issues?labels=agent&state=opened&updated_after=<watermark>`
     (`order_by=updated_at&sort=asc`) per configured group, subgroups included. A labelled issue with
     no existing task becomes a task. Per-group watermark in `ChannelCursors`, with overlap and dedupe
     on issue id exactly as for Jira.
  3. **Task MR state**: for each task in `AwaitingReview`, `GET /projects/:id/merge_requests/:iid` to
     detect merged / closed and finish the task. Only active tasks, so this stays cheap. The same step
     reads the MR's `head_pipeline` (`WatchPipelines`): a **failed** pipeline becomes a follow-up
     instruction naming the failing jobs and quoting the tail of their logs, so the agent fixes its own
     red build on the same branch. Bounded by `MaxPipelineFixAttempts` (2), with the pipeline id recorded
     so one failure is never handled twice; when the attempts run out it says so on the MR and stops.
     `allow_failure` jobs are ignored; running and successful pipelines are left alone.
- Triggers:
  - Issue **assigned to the bot** (to-do `assigned`) or **labelled `agent`** → **task** in that
    project. Both are supported; which the team uses is convention **[decide]**.
  - `@bot` mention in an issue note → question (or follow-up if the issue has an active task).
  - `@bot` mention in an MR note (incl. diff discussions; the poller fetches the discussion to get the
    file/line position) → **follow-up** on that MR's branch, or a question if the MR is not
    agent-owned **[decide]**: allow follow-ups on any MR, or only on MRs the agent opened?
- Replies via Notes API (`/projects/:id/issues/:iid/notes`, `/projects/:id/merge_requests/:iid/notes`),
  markdown passes through. Acknowledge with an 👀 award emoji + short note when a task is picked up.
- Identity: the to-do / note `author` gives user id + username; resolve via `GET /users/:id` and
  prefer the `identities` entry (Keycloak `sub` for OIDC logins, or the LDAP DN), else username.
- Git access: HTTPS with `oauth2:<token>` supplied through a credential helper by the orchestrator at
  clone/push time only; never in the coding loop's environment.
- Idempotency: a to-do is marked done only after its event is stored; note ids and issue ids go into
  `ProcessedEvents`. Edited notes do not re-trigger.
- Load per interval: one to-do call, one issues call per group, one call per awaiting task. Far below
  GitLab's default rate limits; latency is at most one interval.

### 6.6 Host

**Why ASP.NET Core at all?** The channels do not need it: Mattermost is an outbound WebSocket, Jira
and GitLab are polled, and the worker is a background service. The host is therefore a plain
**Generic Host** worker (`Microsoft.Extensions.Hosting`). ASP.NET Core (Kestrel + minimal APIs) is
added for exactly three optional things, and `Api.Enabled=false` removes the listener entirely:

1. the remote CLI API (`/api/chat`, `/api/tasks`), so team members' machines never hold the LLM key,
   bot tokens, or DB access;
2. health endpoints for monitoring (`/healthz`, `/readyz`);
3. the optional MCP server (6.13).

If the CLI only ever runs on the server box in `--local` mode, ASP.NET Core can be left out and the
host is a pure worker service. Kestrel ships with the runtime and adds no external dependency, so the
recommendation is to keep the API behind the flag.

- Endpoints (when enabled): `POST /api/chat`, `POST /api/tasks`, `GET /api/tasks/{id}` (+ log
  stream), `GET /healthz`, `GET /readyz`. No webhook endpoints exist.
- `/api/*` requires a **Keycloak-issued bearer token** (`AddJwtBearer`, authority = the realm,
  audience = the agent API client). Roles come from the token's group/role claims through the same
  `Authorization.Roles` mapping; no directory lookup on this path.
- Hosted services: `MattermostListener` (WebSocket), `JiraPoller`, `GitLabPoller`,
  `AnswerWorker` (fast path queue), and in v1 the `TaskWorker` (6.7). Each inbound service is an
  `IEventSource` that pushes `InboundEvent`s into the same pipeline. Queues on `System.Threading.Channels`; tasks are also persisted so a restart
  resumes `Queued` tasks and marks in-flight ones `Interrupted` for retry.
- Windows service or Linux systemd/container; outbound HTTP with `Microsoft.Extensions.Http.Resilience`.

### 6.7 Task pipeline (`Agent.Core.Tasks` + `Agent.Worker`)

Task record: id, source (`GitLabIssue` / `JiraIssue` / `MergeRequest` / `Cli` / `Mattermost`),
source refs, requester identity, project + repo URL, base branch, work branch, MR ref, status, budget
usage, timestamps, event log.

State machine:

```
Received -> Authorized -> Queued -> Preparing (clone/branch)
        -> Working (coding loop) -> Verifying (build/test) -> Publishing (commit/push/MR)
        -> AwaitingReview --(mention on MR)--> Working ... -> AwaitingReview
        -> Done (MR merged) | Closed (MR closed / ticket closed) | Failed | NeedsInput | Cancelled
```

Flow for a new task:

1. Inbound event (poll / WebSocket) → identity → authz (role `Team`) → create task → ack comment on
   the ticket
   ("Picked up as task #42; I'll open an MR here when done").
2. Worker takes the task (max `Coding.MaxConcurrentTasks`; one active task per repo+branch).
3. Clone (partial clone, `--filter=blob:none`), create `agent/<key>-<slug>` from the default branch
   (or check out the MR source branch for follow-ups).
4. **Announce the plan** (`Coding.PostPlan`, first run only): a short understanding-and-plan note posted
   to the ticket or thread before anything is written, so a person can `!cancel` while the branch is
   still empty. It is not an approval gate — the merge request is — so the task proceeds immediately.
5. Coding loop (6.8) with the ticket text as the instruction and repo guidance from `AGENTS.md`.
6. Verify: run the repo's configured build/test commands; feed failures back to the loop (bounded
   retries).
7. Commit (author = bot; Conventional Commits subject inferred from what was asked, `Coding.ConventionalCommits`;
   trailers `Requested-by`, `Refs: <source ref>`, `Task: #42`), push, open MR (draft if verification
   failed or budget exhausted), set the requester as reviewer, link the issue.
8. Final comment on the ticket and in the MR description: what changed, what was run, what was not
   verified, budget used.
9. **Watch the merge request's pipeline** (6.5). A failed pipeline becomes a follow-up instruction
   carrying the failing jobs and their log tails, bounded by `GitLab.MaxPipelineFixAttempts`; when the
   attempts run out the agent says so on the MR and leaves it for a human.

Follow-ups: an authorized mention on the MR appends the comment (and, for diff discussions, the
referenced file/lines) as the next instruction; the loop runs on the existing branch and pushes.
Unauthorized mentions are logged and ignored.

Concurrency: per-task workspace directory; tasks on the same repo run in parallel only on different
branches.

### 6.8 Coding engine (`Agent.Coding`)

- `ICodingEngine.RunAsync(Workspace, Instruction, Budget, IProgress)` → `CodingResult` (summary,
  changed files, commands run, verification status).
- Loop = `IChatClient` + `UseFunctionInvocation()` with these tools:
  - `list_files(glob)`, `read_file(path, startLine?, endLine?)`, `search(pattern, glob?)`
  - `write_file(path, content)`, `edit_file(path, oldText, newText)` (exact-match replace)
  - `run(command)` — allow-listed executables only, timeout, output truncated
  - `git_diff()`, `git_status()` (read-only; commit/push are orchestrator-only)
  - `done(summary)` — ends the loop
  - MCP tools whose `Scope` includes `Coding` (6.13), e.g. docs search or package-registry lookup
- Phases inside one run: **explore** (read `AGENTS.md`, repo layout, related code) → **plan** (short
  written plan, kept in context) → **implement** → **verify** → `done`.
- Budgets: max turns, max wall time, max total tokens, max `run` invocations; on exhaustion the loop
  stops and the MR is opened as **Draft** with an explanation.
- Context management: tool output truncation, rolling summary of older turns, file-read cache.
- Repo guidance: `AGENTS.md` at the repo root is prepended to the system prompt (conventions, how to
  test). Structured commands in `.agent/config.yml` **[decide]**:

  ```yaml
  build: dotnet build -warnaserror
  test:  dotnet test --no-build
  protectedPaths: [".gitlab-ci.yml", "**/*.pfx", "deploy/**"]
  ```

  Missing config → sensible detection (`*.sln` → dotnet, `package.json` → npm, `Makefile` → make).

#### 6.8.1 The opencode engine (`Coding:Engine = OpenCode`)

Instead of running our own loop, the agent hands the task to the **opencode** CLI inside the same sandbox
and keeps everything around it: clone, branch, verification, commit, push, merge request. opencode never
sees a git credential and never commits, exactly as with the native engine.

What the orchestrator still guarantees, and how:

| Guarantee | How it survives the handover |
|-----------|------------------------------|
| Isolation | The same sandbox (6.9). opencode runs as a command inside it, so the container is the outer boundary. |
| Executable allow list | Translated into opencode's `permission.bash` rules: catch-all `deny`, then an `allow` per allowed executable, then `deny` for `git commit`, `push`, `reset`, `checkout`, `rebase` and `config`, which win because later rules take precedence. |
| Protected paths | Translated into `permission.edit` deny rules, **and** re-checked against `git status` after the run. A protected file that changed fails the task and is never published. |
| Wall-clock budget | `Budget.MaxMinutes` becomes the command timeout. |
| Verification | Run by us afterwards; a failure is fed back with `opencode run --continue`, bounded by `Budget.MaxVerifyRetries`. |
| No prompting | `permission.question` is denied and `--auto` is passed, so a non-interactive run cannot hang waiting for input. |
| Telemetry | The same `agent.coding.run` span, tagged `agent.coding.engine=opencode`. |

What is genuinely lost, and should decide whether you use it:

- **Turn, token and run-count budgets.** opencode owns the loop, so only the wall clock is enforceable.
  Token counts are best-effort, scraped from `--format json`, and may be zero.
- **Per-command policy at our layer.** The bash rules are enforced *by opencode*, not by us. In `Docker`
  mode the container is still a hard boundary; in `Process` mode it is the only barrier, so the engine logs
  a warning on every run. Treat `Docker` as required in practice.
- **The API key enters the sandbox.** opencode has to call the model, so the key is passed per command as an
  environment variable. On the container path it is visible in the `docker exec` arguments.

Configuration is generated per run into `opencode.agent.json` at the repository root, pointed at by
`OPENCODE_CONFIG`, added to `.git/info/exclude` so it can never be committed, and deleted afterwards. It
declares the team endpoint as an `@ai-sdk/openai-compatible` provider, pins the coding model, lists
`AGENTS.md` under `instructions`, and carries the permission block above. A missing `opencode` binary fails
the task with a message naming the fix rather than silently falling back to the native loop.

### 6.9 Workspace and sandboxing

v1 (process isolation, any OS):

- Each task gets `Coding.WorkspaceRoot/<taskId>`; deleted after completion (kept on failure for
  `Coding.KeepFailedWorkspacesDays`).
- Commands run via `CliWrap` with a scrubbed environment (no agent secrets), working directory pinned
  to the workspace, path arguments validated to stay inside it.
- `run` allow-list (config): `git`, `dotnet`, `node`, `npm`, `npx`, `make`, `python`, … plus per-repo
  additions from `.agent/config.yml`. Denied: anything else, shell redirections to outside paths.
- Protected paths (global + per-repo): the agent may read but not modify CI config, deploy
  manifests, secrets; violations fail the task.
- The worker runs as a low-privilege service account with write access only to the workspace root.

**Container per task (implemented).** `Coding:Sandbox:Mode` selects where the model's commands run:
`Process` (the v1 behaviour above, the default) or `Docker`. Only the `run` tool and build/test
verification go through the sandbox — clone, branch, commit, push, and every file edit stay with the
orchestrator on the host, so **the container never sees a git credential**.

- One container per coding run: `docker run --detach` … `sleep infinity`, each command a `docker exec`,
  removed with `docker rm --force` when the run ends (including on failure or cancellation).
- The repository is bind-mounted at `WorkDir` (default `/work`), which is also the working directory.
  The paths the model reads and writes are identical in both modes.
- Isolation defaults: `--cap-drop ALL`, `--security-opt no-new-privileges`, `--memory 4g`, `--cpus 2`,
  `--pids-limit 512`, `--init`, and a `/tmp` tmpfs. `ReadOnlyRootFilesystem` and `User` (e.g. `1000:1000`)
  are opt-in; `ExtraArgs` passes anything else through to `docker run`.
- **Egress** is `Network` (default `bridge`): set it to `none` to block egress entirely, or to a network
  whose egress is restricted to GitLab and the package registries. With `none`, pre-populate a package
  cache through `Volumes` (e.g. `agent-nuget:/root/.nuget/packages`) or restores will fail.
- **Image per toolchain**, chosen from the repository's own build/test commands: `dotnet`, `node`,
  `python`, `go`, `rust`, else `DefaultImage`. Override any entry in `Images` to point at an internal
  registry.
- **Per-repository containers via `.engex.yml`** (6.9.1) when the toolchain default is not enough.
- Timeouts are enforced inside the container with coreutils `timeout -s KILL`, so a hung process dies
  there rather than being orphaned when the client gives up; the host wait is deliberately longer.
- Starting the sandbox is part of the run: if `docker run` fails (no daemon, image missing, pull
  timeout), the task fails with that message instead of silently running on the host.
- Requires a Linux worker, Docker Desktop, or WSL2 (open question 4). `!status` reports the daemon
  version, or why it is unavailable, so a misconfigured host is visible before a task is queued.
- **Operational prerequisite:** the daemon must be allowed to bind-mount `Coding:WorkspaceRoot`
  (Docker Desktop → Resources → File sharing). An unshared path makes `docker run` *hang* rather than
  fail — verified on a real daemon during implementation — so the start timeout is what catches it, and
  its message names the path.

#### 6.9.1 `.engex.yml` — what a repository may ask for

A repository declares its own build, policy, and **container** in `.engex.yml` at its root
(`.engex.yaml` and the older `.agent/config.yml` are also accepted; the first one found wins):

```yaml
build: dotnet build -warnaserror
test: dotnet test --no-build
container:
  profile: dotnet-node       # a name from the host's menu
  memory: 6g
  network: none
allowedExecutables: [pwsh]
protectedPaths: [deploy/**]
```

Three sources feed a run and they are deliberately separate. **The task** comes from the requester
(ticket, mention, follow-up) and only from there. **`.engex.yml`** carries structured settings: how the
project builds, the container it needs, what the agent may touch. **`AGENTS.md`** carries standing
repository conventions, the things that would otherwise be rediscovered every task or repeated in every
ticket. Prose never goes in the YAML, and neither repository file can tell the agent what work to do.

The file is **repository content, so it is a request, not a decision**: anyone who can open a merge
request can edit it, and a container image is arbitrary code on the worker. `SandboxPolicy` grants
narrowing and refuses widening, failing the task with an explanatory message rather than silently
running somewhere the repository did not ask for:

| Key | Rule |
|-----|------|
| `profile` | Must exist in `Coding:Sandbox:Profiles`. Unknown → task fails, listing the available names. |
| `image` | Must match a glob in `Coding:Sandbox:AllowedImages`, **empty by default** so raw images are refused until an operator opts in. |
| `memory`, `cpus` | Honoured downward, clamped at `MaxMemory` / `MaxCpus`. |
| `network` | Only `none` (closing) or the host's current value. Opening → task fails. |
| `env` | Merged, with host values winning on collision. |
| mounts | Not settable by a repository at all; cache volumes belong to a host profile. |

`.engex.yml` is in the default protected paths, so the coding agent can read it but cannot edit the
policy it is running under.

### 6.10 Persistence (`Agent.Persistence`)

**Settled: EF Core**, used only for state that must survive a restart. Everything else stays in
memory or in files.

Must be durable (EF Core):

| Table | Why it must persist |
|-------|---------------------|
| `Tasks` | Coding tasks run for minutes and must resume after a restart; MR/branch refs are needed for follow-ups days later. |
| `TaskEvents` | State changes, verification results, budget usage per task — the record posted to the ticket and used for `!task <id>`. |
| `ChannelCursors` | Jira per-project watermark, GitLab per-group watermark, Mattermost last `create_at`. Losing these means re-processing or missing events. |
| `ProcessedEvents` | Idempotency for polled items and WebSocket posts (channel + object id). Prevents double replies after restarts and overlap windows. |
| `Audit` | Every allow/deny decision and every command / tool invocation with caller and role. |

Deliberately **not** persisted:

- Conversation history — rebuilt from the Mattermost thread, Jira issue, or GitLab discussion on each
  request.
- The LLM message list during a coding task — in memory; only summaries land in `TaskEvents`.
- AD role / group cache — `IMemoryCache` with TTL.
- Command, MCP, and prompt definitions — files on disk, hot-reloaded.

EF Core specifics:

- `AgentDbContext` in `Agent.Persistence`; `Agent.Core` only sees interfaces (`ITaskStore`,
  `ICursorStore`, `IProcessedEventStore`, `IAuditSink`).
- **Provider: SQL Server** (`Microsoft.EntityFrameworkCore.SqlServer`) from day one; the same engine
  in dev, test, and prod, so there is no provider switch to test. Connection with **Windows
  authentication** from the service account (no password in config) **[decide]**, or a SQL login held
  in secrets.
- Dev: SQL Server LocalDB or a `mcr.microsoft.com/mssql/server` container; tests use
  `Testcontainers.MsSql` in CI or LocalDB locally.
- Migrations checked in (`dotnet ef migrations add`). Applied at startup with `Database.Migrate()`
  for v1; if DBAs own the schema, ship idempotent scripts (`dotnet ef migrations script --idempotent`)
  instead **[decide]**.
- Concurrency: `rowversion` on `Tasks` for optimistic concurrency; the worker claims queued tasks with
  an atomic `UPDATE … WHERE Status = 'Queued'`, so a second worker instance later needs no code change.
- Retention job for `TaskEvents` / `Audit` / `ProcessedEvents` **[decide]** default 90 days, run by
  an in-app hosted service (or a SQL Agent job if the DBAs prefer).

### 6.11 CLI

- Questions: `agent chat` (REPL, streamed), `agent ask "…"`.
- Tasks: `agent task create --repo <url> "instruction"`, `agent task list`, `agent task show <id>`,
  `agent task log <id> --follow`, `agent task cancel <id>`.
- Commands: `!…` works inside `agent chat` exactly as in Mattermost; `agent cmd <name> [args]` runs
  any command non-interactively. The `task` subcommands above are thin wrappers over `!tasks`,
  `!task`, `!cancel`, so there is one implementation.
- Local coding (optional, nice to have): `agent code "…"` runs the coding engine against the current
  directory with no branch/MR automation — a local assistant that shares prompts and tools with the
  server.
- Auth: `agent login` runs the OAuth **device authorization flow** against Keycloak (prints a URL +
  code, or opens the browser); tokens are cached per user (DPAPI-protected on Windows, `0600` file on
  Linux) and refreshed silently; `agent logout` clears them. The CLI is a public client, no secret.
- `--server <url>` by default; `--local` for in-process development (identity = the logged-in OS
  user, roles from the configured provider).
- `Spectre.Console` + `Spectre.Console.Cli`.

### 6.12 `!commands`

Any message whose text (after removing the bot mention) starts with the command prefix (`!`,
configurable) is a command, not an LLM prompt. The syntax is the same everywhere: Mattermost DM
`!status`, channel `@agent-bot !tasks --all`, Jira comment `[~agent-bot] !cancel 42`, GitLab note
`@agent-bot !review`, CLI REPL `!status`, shell `agent cmd status`. `!` rather than `/` because
Mattermost and GitLab reserve `/` for their own slash and quick actions.

**Dispatch.** identity → authz → `CommandDispatcher` → (no prefix match) question / task
classification. A prefixed but unknown command gets a one-line "unknown command, try `!help`" reply.
The dispatcher tokenizes (quotes, `--flag`, `--flag value`, `-f`), looks the name up in the
`CommandRegistry`, checks the command's role against the caller's roles, binds arguments, runs it, and sends
the `CommandResult` (markdown) through the channel formatter.

**Three ways to define a command**, all landing in the same registry:

1. **Attributed method** — the everyday way. Auto-discovered from scanned assemblies:

   ```csharp
   [Command("tasks", "List coding tasks", Aliases = ["t"], Role = Role.Users)]
   public async Task<CommandResult> Tasks(
       CommandContext ctx,                      // caller, channel, conversation, cancellation
       [Option("all", "Everyone's tasks")] bool all = false,
       [Option("limit")] int limit = 10)
   { … return CommandResult.Markdown(table); }

   [Command("fix", "Turn this issue into a coding task", Role = Role.Team,
            Channels = [Channel.GitLab, Channel.Jira])]
   public Task<CommandResult> Fix(CommandContext ctx, [Rest] string extraInstructions) { … }
   ```

   Binding rules: positional parameters in declaration order; `[Option]` for flags; `[Rest]` takes the
   remaining text; optional parameters get defaults. Supported types: `string`, `int`, `bool`, enums,
   `TaskId`, `IssueRef` (`PROJ-12`, `group/repo#34`, `!56`), `Uri`. Usage text is generated from the
   signature, so `!help tasks` needs no extra work.

2. **YAML prompt template** — no code, no rebuild, hot-reloaded from `commands/*.yaml`:

   ```yaml
   name: summarize
   aliases: [tldr]
   description: Summarise the current thread / issue / MR
   role: users                    # users | team | admin  (required)
   channels: [mattermost, jira, gitlab, cli]
   model: AnswerModel             # AnswerModel | CodingModel | a concrete model name
   tools: [jira_get_issue, gitlab_get_mr]
   prompt: |
     Summarise the discussion below in at most 5 bullets, then list open questions.
     Extra guidance from the caller: {{args}}
     ---
     {{context}}
   ```

   Placeholders: `{{args}}`, `{{context}}` (thread / issue / MR text), `{{caller}}`, `{{issue}}`,
   `{{repo}}`, `{{branch}}`. `kind: task` makes the rendered prompt a coding-task instruction instead
   of a question, which is how `!fix` and `!implement` are defined without C#.

3. **`ICommand` implementation** registered in DI, for commands that need constructor injection,
   streaming output, or multi-step interaction. Same metadata via properties instead of attributes.

**Metadata** on every command: name, aliases, description, generated usage, `Role` (`Users` / `Team` /
`Admin`; required), allowed channels, `Hidden`, and `ExposeAsTool` — when true the command is also registered
as an LLM function, so the model can call `!status` logic during a normal conversation.

**Built-ins** (M1–M2) and their roles:

| Role | Commands |
|------|----------|
| `Users` | `!help [cmd]`, `!ping`, `!whoami` (resolved AD identity + roles — the first thing to run when authz misbehaves), `!status` (queue depth, active tasks, poll health), `!tasks` (own), `!task <id>`, `!model` (show) |
| `Team` | `!tasks --all`, `!cancel <id>` / `!retry <id>` (own tasks), `!fix` / `!implement` (YAML; start a task from the current issue), `!model <name>` (set for this conversation), `!mcp list`, `!mcp tools <server>` |
| `Admin` | `!cancel` / `!retry` on anyone's task, `!model set <name> --global`, `!reload` (commands, MCP definitions, options), `!mcp reload`, `!mcp call …` |

**Rules.** Role is checked before argument parsing, denied exactly like any other request and audited.
Binding errors return the usage line. Exceptions are logged with a short reference and the user sees
`!tasks failed (ref 7f3a)`. The registry validates at startup (duplicate names/aliases, unknown tools
or placeholders in YAML) and fails fast; on hot reload a broken file is skipped and logged. `!help`
lists only what the caller may run on that channel.

### 6.13 MCP tools (Model Context Protocol)

MCP is how the LLM gains capabilities without C#: point the agent at an MCP server and its tools
appear to the model next to the native tools and `ExposeAsTool` commands. That covers existing
servers (GitLab, Jira, filesystem, docs search, database query, internal APIs) and small team-written
ones in any language.

**Client side (M1).**

- Official C# SDK (`ModelContextProtocol`). Its `McpClientTool` derives from `AIFunction`, so an MCP
  tool drops straight into `ChatOptions.Tools` beside the native functions; no adapter code.
- Transports: `Stdio` (command + args + env, for local servers launched via `npx`, `uvx`,
  `dotnet run`, …) and `Http` (Streamable HTTP / SSE; URL + headers for remote servers).
- Definitions live in `Mcp.Servers` (appsettings, `IOptionsMonitor`) or as drop-in files in
  `mcp/*.json`, one per server; both hot-reload, mirroring the `commands/` directory.
- `McpToolProvider` connects each enabled server at startup and on reload, lists its tools,
  subscribes to `tools/list_changed`, and registers each one in the shared `IToolRegistry` as
  `<server>__<tool>` (sanitised to the `[A-Za-z0-9_-]{1,64}` function-name rule). One long-lived
  client per server; reconnect with backoff; health visible in `!status` and `!mcp list`.
- Every MCP tool is wrapped in a **governed tool** decorator before the model sees it:
  - **Authorization**: server-level `Role` (Users / Team / Admin) with per-tool overrides. A tool the
    caller may not use is not offered at all, so the model cannot even attempt it.
  - **Filtering**: `Tools.Allow` / `Tools.Deny` globs; `Scope` (`Answer`, `Coding`) selects which
    loops receive it; optional `Channels`.
  - **Limits**: per-call timeout, argument and result size caps (`MaxResultChars`, truncated with a
    marker), and calls charged against the same per-turn / per-task budgets as native tools.
  - **Audit**: server, tool, caller, duration, and a hash of args/result per invocation.
  - **Untrusted output**: results are data, never instructions, the same rule as tickets and repo
    files.
- YAML `!commands` may list MCP tools in `tools:` by qualified name (`docs__search`), so a prompt
  command is scoped to exactly the tools it needs.
- Coding loop: servers with `Scope: Coding` are offered to the coding engine. Stdio servers that need
  workspace access start with the task workspace as working directory and a scrubbed environment;
  under the M9 container sandbox they run inside the container.
- Secrets: `Headers` for HTTP servers and `Env` for stdio servers come from configuration secrets like
  every other credential; a stdio child process receives only the env listed for it.
- v1 uses MCP **tools** only. MCP **prompts** can later surface automatically as `!<server>.<prompt>`
  commands, and MCP **resources** as providers for `{{context}}`.

**Commands.** `!mcp list` (servers, status, tool counts), `!mcp tools <server>` (name, description,
role), `!mcp reload` (Admin), `!mcp call <server> <tool> <json>` (Admin, for debugging; audited like
any other call).

**Server side (optional, M8).** The agent can also *be* an MCP server: `ModelContextProtocol.AspNetCore`
exposes the native `jira_*` / `gitlab_*` tools, `ExposeAsTool` commands, and optionally pass-through
MCP tools over HTTP, authenticated with Keycloak bearer tokens. IDE assistants and other agents on the intranet then use the
team's tools with the same AD authorization and audit, without holding their own Jira/GitLab
credentials.

**Example definition** (`mcp/docs.json`):

```jsonc
{
  "Name": "docs",
  "Transport": "Stdio",
  "Command": "npx", "Args": ["-y", "@team/docs-mcp"],
  "Env": { "DOCS_ROOT": "D:\\docs" },
  "Scope": ["Answer", "Coding"],
  "Role": "Users",
  "Tools": { "Allow": ["search*", "read*"], "Deny": [], "Roles": { "read_private*": "Team" } },
  "TimeoutSeconds": 30
}
```

### 6.14 Observability (OpenTelemetry)

The agent emits traces, metrics, and logs through **OpenTelemetry**. `Agent.Core` only *emits*: it depends
on `ActivitySource` and `Meter` from the base class library, never on the OpenTelemetry SDK, so the host
alone decides what is exported. With no collector configured the instrumentation still runs and ships
nothing, which keeps it safe to leave in everywhere.

**Traces.** One `agent.event` span per inbound message, with `agent.command`, `agent.answer`,
`agent.tool`, `agent.coding.run`, and `agent.sandbox.command` beneath it. The chat client adds `gen_ai`
spans through `Microsoft.Extensions.AI`'s own instrumentation, so a question links its model calls and
tool calls in one trace. Outbound HTTP to Mattermost, Jira, GitLab, Keycloak, and the LLM endpoint is
instrumented, as are ASP.NET Core requests and SQL Server calls. Health endpoints are excluded by default.

**Metrics.** Named for the question each answers:

| Instrument | Answers |
|------------|---------|
| `agent.events`, `agent.event.duration` | Throughput and latency per channel, kind, and outcome. |
| `agent.events.skipped` | Idempotency drops, i.e. how often a poller re-sees the same item. |
| `agent.authorizations` | Allow and deny counts by action and required role. |
| `agent.commands`, `agent.command.duration` | Which `!commands` are used, and which fail. |
| `agent.tokens` | Token spend by purpose, channel, model, and direction. Cost per channel is the question operators actually ask, which per-model `gen_ai` metrics cannot answer. |
| `agent.tools`, `agent.tool.duration` | Tool usage and failures, including MCP servers by source. |
| `agent.tasks`, `agent.task.duration` | Coding tasks by transition, stop reason, and whether verification passed. |
| `agent.sandbox.commands` | Commands run per sandbox mode, and how many time out. |
| `agent.polls` | Poll cycles per channel. A healthy poll that finds nothing still counts, so **silence on this metric means a broken poller**. |
| `agent.queue.depth` | Backlog waiting for the pipeline. |

**Logs** are exported through the OpenTelemetry logging provider with scopes, so a log line carries the
trace id of the event that produced it.

**Content stays out.** Spans carry identifiers, roles, counts, and outcomes, never message text, ticket
bodies, or file contents. Prompts and completions reach the `gen_ai` spans only when
`Llm:EnableSensitiveTelemetry` is turned on deliberately. Exceptions record their type, not their message.

Configured under `Telemetry`: an OTLP endpoint (grpc or http/protobuf, with headers for a hosted backend),
per-signal switches, a sample ratio, and a console exporter for development. The standard
`OTEL_EXPORTER_OTLP_ENDPOINT` environment variable is honoured.

## 7. Security model

The agent now executes commands and pushes code, so the threat model matters more than in v1.

| Threat | Control |
|--------|---------|
| Unauthorized person triggers work | `Team` role check (via AD groups) on the *requester of each instruction*, including follow-ups; audit log. |
| Prompt injection via ticket text, repo files, or comments from unauthorized users | Only text from authorized requesters is treated as instruction; everything else is data. Read-only Q&A tools; allow-listed commands; protected paths; no secrets in the loop's environment; human MR review. |
| Exfiltration via CI config edits | `.gitlab-ci.yml` and deploy manifests are protected paths by default. |
| Bot token misuse | Bot is Developer, not Maintainer; protected branches; token scoped to needed groups; token used only by the orchestrator for clone/push/API, never exposed to tools. |
| Runaway cost / loops | Per-task budgets (turns, tokens, time, `run` count); global concurrency cap; per-user daily task cap **[decide]**. |
| Destructive commands | Allow-list + workspace-only paths; no `rm`-style tools; git reset/clean only by orchestrator. |
| Compromised build script / dependency | Container per task (6.9): capabilities dropped, memory/CPU/PID limits, configurable egress (`none` blocks it), and no credential inside the container — git and tokens stay on the host. |
| Inbound spoofing | All channels are pull-based (WebSocket client or polling); there is no inbound endpoint to spoof. The CLI API requires a Keycloak-issued bearer token, is intranet-only, and can be disabled. |
| Traceability | Commit trailers (`Requested-by`, `Task`), MR description with full summary, audit + task event log. |
| Command abuse | Role-gated commands (Users / Team / Admin) checked before parsing; typed binding rejects unexpected input; YAML commands can only use registered tools; admin commands audited. |
| MCP tool misuse | Tools filtered per caller role before the model sees them; allow/deny globs; timeouts and result caps; per-call audit; stdio servers get a scrubbed env; tool output treated as untrusted. Side-effecting servers should be `Role: Team` or higher with an explicit allow list. |

## 8. Configuration (excerpt)

```jsonc
{
  "Llm":  { "BaseUrl": "https://llm.internal/v1", "ApiKey": "<secret>",
            "AnswerModel": "…", "CodingModel": "…",
            "Overrides": { "Jira.Answer": "…" },          // optional, per purpose/channel
            "AllowClientModelOverride": false, "TimeoutSeconds": 120 },   // hot-reloaded via IOptionsMonitor
  "Authorization": { "Provider": "Keycloak",                       // Keycloak | Ldap
                     "Roles": { "Users": ["/agent/users"], "Team": ["/agent/team"], "Admin": ["/agent/admin"] },
                     "CacheMinutes": 10, "DenyBehaviour": "Silent" },
  "Keycloak": { "Authority": "https://sso.corp.local/realms/corp", "ApiAudience": "agent-api",
                "CliClientId": "agent-cli",
                "Admin": { "ClientId": "agent-service", "ClientSecret": "<secret>" } },
  "Api":      { "Enabled": true, "ListenUrl": "http://0.0.0.0:5080" },
  "Commands": { "Prefix": "!", "Directory": "commands", "ReplyToUnknown": true },
  "Mcp":  { "Directory": "mcp", "MaxResultChars": 20000,
            "Servers": { "inventory": { "Transport": "Http", "Url": "https://mcp.internal/inventory",
                                        "Headers": { "Authorization": "Bearer <secret>" },
                                        "Role": "Team", "Scope": ["Answer"],
                                        "Tools": { "Deny": ["delete_*"] } } } },
  "Ldap": { "Enabled": false, "Server": "dc01.corp.local", "Port": 636, "UseSsl": true,
            "BaseDn": "DC=corp,DC=local", "BindDn": "…", "BindPassword": "<secret>" },   // fallback provider only
  "Mattermost": { "BaseUrl": "…", "BotToken": "<secret>", "RespondToDirectMessages": true,
                  "RespondToMentions": true, "GroupMessagesRequireMention": true,
                  "ThreadFollowMinutes": 120, "DmHistoryMessages": 30, "ChannelAllowList": [],
                  "AckReaction": "eyes", "StreamByEditing": false },
  "Jira": { "BaseUrl": "…", "Username": "agent-bot", "Token": "<secret>",
            "PollSeconds": 30, "OverlapMinutes": 2, "Projects": ["PROJ", "OPS"],
            "TaskLabel": "agent", "RepositoryField": "customfield_12345",
            "ProjectRepos": { "PROJ": "https://gitlab.internal/team/proj" } },
  "GitLab": { "BaseUrl": "https://gitlab.internal", "Token": "<secret>", "BotUsername": "agent-bot",
              "PollSeconds": 30, "Groups": ["team"], "TaskLabel": "agent", "TaskOnAssign": true,
              "FollowUpsOnForeignMrs": false },
  "Coding": { "WorkspaceRoot": "D:\\agent-work", "MaxConcurrentTasks": 2,
              "Budget": { "MaxTurns": 60, "MaxTokens": 400000, "MaxMinutes": 30, "MaxRuns": 25 },
              "AllowedExecutables": ["git","dotnet","node","npm","npx","make","python"],
              "ProtectedPaths": [".gitlab-ci.yml","**/*.pfx","**/appsettings.Production.json"],
              "BranchPrefix": "agent/", "OpenAsDraft": false, "KeepFailedWorkspacesDays": 3 },
  "Persistence": { "ConnectionString": "Server=sql01.corp.local;Database=Agent;Integrated Security=true;Encrypt=true" }
}
```

## 9. Key NuGet packages

| Area | Package |
|------|---------|
| LLM | `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `OpenAI` |
| MCP | `ModelContextProtocol` (official C# SDK; client tools are `AIFunction`s), `ModelContextProtocol.AspNetCore` (optional server side) |
| Hosting | `Microsoft.Extensions.Hosting(.WindowsServices)`, `Microsoft.Extensions.Http.Resilience` |
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer` (API), `Duende.IdentityModel` (CLI device-flow helpers, optional), `System.DirectoryServices.Protocols` (LDAP fallback) |
| Coding | `CliWrap` (processes; git via the CLI, not LibGit2Sharp), `Microsoft.Extensions.FileSystemGlobbing` |
| Data | `Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.EntityFrameworkCore.Design` (migrations tooling); `Testcontainers.MsSql` for tests |
| Formatting / YAML | `Markdig`, `YamlDotNet` |
| CLI | `Spectre.Console`, `Spectre.Console.Cli` |
| Tests | `xunit`, `NSubstitute`, `WireMock.Net`, `Microsoft.AspNetCore.Mvc.Testing` |

## 10. Testing strategy

- **Core / Coding**: scripted fake `IChatClient` (returns planned tool calls); coding-engine tests run
  against a temp git repo on disk — assert edits, protected-path refusal, allow-list enforcement,
  budget stops, and the resulting diff.
- **Pipeline**: end-to-end task test with a local bare repo as "GitLab remote" and WireMock for the
  GitLab/Jira APIs: labelled issue appears in a poll → branch pushed + MR API called + comment posted.
- **Channels**: recorded fixtures (GitLab to-dos, issues, notes, discussions; Jira search and issue
  responses) exercising both pollers, including watermark overlap, restart, and already-processed
  cases; WireMock stubs; WebSocket listener over an in-memory pair.
- **Commands**: tokenizer and binder table tests (quotes, flags, defaults, bad input → usage);
  registry validation (duplicates, missing role, unknown tools/placeholders); YAML hot-reload; role enforcement per
  channel; `!help` filtering.
- **MCP**: an in-process test MCP server (SDK in-memory transport) with a few tools; tests for name
  sanitising, allow/deny globs, role filtering (tool absent from the offered list), timeouts, result
  truncation, reconnect after server exit, and hot reload of `mcp/*.json`.
- **Host**: `WebApplicationFactory` for bearer token required on `/api`, role mapping from claims,
  task creation, log streaming, and `Api.Enabled=false` leaving no listener.
- **Keycloak**: WireMock stub of the discovery document, device-flow token endpoint, and Admin API;
  token validation with a test signing key; group → role mapping.
- **LDAP**: opt-in integration test against a real DC (`AGENT_LDAP_TESTS=1`).
- **Persistence**: EF Core tests against a real SQL Server (`Testcontainers.MsSql` in CI, LocalDB
  locally): migrations from scratch, task claiming under concurrency, cursor and idempotency stores.
- **Mattermost**: DM without mention, channel mention → threaded reply, thread continuation inside
  and outside the follow window, long-reply splitting, reconnect gap re-fetch.
- **Model quality**: a small benchmark set of real past tickets, run manually per model change.

## 11. Milestones **[decide]** ordering

| # | Milestone | Outcome |
|---|-----------|---------|
| M0 | Skeleton | Solution, projects, config/options, logging, persistence, CI build + test. |
| M1 | Core + CLI (local) | `agent ask` works against the endpoint; confirms tool calling and model fitness (A6). Command framework with `!help`, `!ping`, `!model`, and YAML prompt commands with hot reload. MCP client: tools from one configured stdio server usable in `agent ask`. |
| M2 | Authorization | Keycloak group provider (Admin API) with LDAP fallback, role resolution from groups, cache, audit, role gating on commands/tools; CLI denies non-members; `!whoami`. |
| M3 | GitLab channel (answers) | Poller (to-dos + labelled issues), replies on issues/MRs, identity → AD. |
| M4 | Coding engine + task pipeline | GitLab issue labelled `agent` → branch → MR → comment. Process-isolated workspaces, budgets, protected paths. |
| M5 | MR follow-ups | Mention on an agent MR revises the branch; merged/closed events close the task. |
| M6 | Jira channel | Answers on Jira; Jira issue → task with repo resolution; MR link posted back. |
| M7 | Mattermost | Answers; optional task creation from chat. |
| M8 | Host API + remote CLI | `/api/chat`, `/api/tasks` with Keycloak bearer auth + streaming; `agent login` device flow; CLI defaults to remote. Optional: expose the agent's tools as an MCP server on the same host. |
| M9 | Hardening | Container-per-task sandbox, per-user caps, retention, Windows service / systemd packaging, runbook. |

Rationale: the coding path (M4) is the highest-value and highest-risk piece, so it comes right after
authz and the first channel that can exercise it end-to-end. Mattermost moved later because it only
adds a surface, not a capability. Reorder if the team wants chat answers first.

## 12. Open questions **[decide]**

1. **GitLab** (polling — settled): version? Bot user + token; is login via Keycloak (then usernames
   match exactly) or LDAP?
   Which groups are in scope? Task trigger convention: assign the issue to the bot, add a label, or
   both? Is a 30 s poll interval acceptable?
2. **Jira** (DC, bot account, polling — settled): which projects to poll; how to indicate the target
   repo (custom field vs. text vs. project map); is a 30 s poll interval acceptable?
3. **Mattermost** (bot account over WebSocket, DMs and threads — settled): AD-backed login? Thread
   continuation without re-mention acceptable, and for how long? Group messages: mention required?
   Stream replies by editing the post?
4. **Worker hosting**: Linux VM with Docker (recommended for M9 sandboxing) or Windows? Which
   toolchains must be installed?
5. **Identity** (on-prem AD behind Keycloak — settled): does Keycloak's LDAP federation import AD
   groups, and can a group-membership mapper be added to the agent clients' tokens? Which realm? Can
   we register `agent-cli` (public, device flow) and `agent-service` (confidential, `view-users`)?
   Do Mattermost, GitLab, and Jira log in through Keycloak? Keep direct LDAP as a fallback?
6. **LLM endpoint**: server type, models available for answering vs. coding, context window, tool
   calling and streaming support, rate/token budgets.
7. **MR conventions**: draft by default or ready? Reviewer = requester? Branch naming? Should the
   agent add `Closes #n`? Required approvals already enforced by GitLab?
8. **Follow-ups on MRs the agent didn't open**: allowed?
9. **Who may instruct a task**: only the original requester, or anyone with the `Team` role?
10. **Budgets**: acceptable per-task time/token limits and per-user daily caps.
11. **Repo config**: adopt `AGENTS.md` + `.agent/config.yml` in repos?
12. **Persistence** (EF Core + SQL Server — settled): which instance and database; Windows auth via
    the service account or a SQL login; app-applied migrations or DBA-run scripts?
13. **Commands**: which commands beyond the built-ins on day one (`!summarize`, `!review`, `!fix`,
    `!explain`, …)? Which AD groups map to `Users`, `Team`, and `Admin`? Keep `!` as the prefix?
14. **MCP**: which servers first (existing internal ones? GitLab/Jira MCP servers, docs, databases)?
    Are `node`/`npx` or `uv` available on the host for stdio servers, or HTTP servers only? Is
    exposing the agent itself as an MCP server wanted?

## 13. Risks

- **Model capability**: a weak or small-context model makes the coding path produce low-quality MRs;
  mitigate by verifying in M1 and keeping `AnswerModel` / `CodingModel` separate.
- **Prompt injection** now has real consequences (code, commands). Controls in section 7; the MR
  review gate is the backstop and must not be weakened (no auto-merge, ever).
- **Toolchain diversity** across repos makes the worker hard to provision; containers per toolchain
  (M9) are the fix; until then keep the in-scope repo list short.
- **Identity mapping gaps** (redacted emails, non-AD accounts) would block legitimate users or, worse,
  mis-authorize; fail closed and log.
- **Long tasks vs. restarts**: tasks are persisted and resumable at phase boundaries; a task
  interrupted mid-loop restarts from the branch state, not from scratch.
- **Concurrent edits on one branch** (human and agent) — the agent rebases before pushing and stops
  with a comment if it cannot.

## 14. Implementation status (2026-09-04)

Everything below builds from `agent.slnx` and is covered by tests that mock every external system
(WireMock.Net for HTTP, a scripted `IChatClient` for the LLM, SQLite for EF Core, an in-memory socket for
Mattermost, in-process MCP servers over pipes, local bare git repositories for the worker).

| Component | Project | Tests |
|-----------|---------|-------|
| Core: pipeline, roles, commands, tools, tasks, LLM factory, telemetry | `Agent.Core` | 129 |
| Mattermost (WebSocket, threads, DMs, reactions, splitting) | `Agent.Channels.Mattermost` | 116 |
| Jira DC (polling, wiki formatter, tools, repo resolver) | `Agent.Channels.Jira` | 164 |
| GitLab (to-do polling, labelled issues, MR publisher, pipeline watch, `!fix`, tools) | `Agent.Channels.GitLab` | 211 |
| Coding engine + worker (native and opencode engines, plan step, workspace, git, budgets, MR flow, container sandbox, `.engex.yml` policy) | `Agent.Coding`, `Agent.Worker` | 254 |
| Persistence (EF Core, SQL Server migration), Keycloak, LDAP | `Agent.Persistence`, `Agent.Infrastructure.*` | 60 |
| MCP client, governance, `!mcp` | `Agent.Mcp` | 124 |
| Host API (JWT bearer, chat/SSE, tasks) and CLI remote backend | `Agent.Host`, `Agent.Cli` | 18 |
| **Total** | | **1076, all passing** |

Milestone mapping: M0–M8 are implemented, plus the M9 per-task **container sandbox** (6.9;
`Coding:Sandbox:Mode = Docker`, default stays `Process`) and per-repository containers through `.engex.yml`
(6.9.1). Still open from M9: per-user daily caps and the Windows service packaging script. The optional MCP *server* side (6.13) is not built.

Deviations from the text above worth knowing:

- Mattermost's `StreamByEditing` is accepted in configuration but inert; the core has no streaming reply
  contract for channels (the CLI/API do stream).
- A mention on a merge request the agent did not open starts a new task on that branch when
  `GitLab:FollowUpsOnForeignMrs` is true (handled in the core pipeline).
- Re-adding the task label to an issue whose task already finished does not start a second task; use a
  mention with instructions (follow-up) or `!retry`.
- Jira JQL timestamps use the bot user's profile time zone (`Jira:TimeZone` overrides it).
- The worker rebuilds the requester's roles from task metadata (`RequesterRoles`) for role-filtered tools.
- Task recovery on start assumes one worker process per database.
- Verification commands run under the same allow-list as the `run` tool: no shell operators; put multi-step
  verification in a script or build target.
