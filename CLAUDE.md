# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET 10 team agent. It answers questions and runs coding tasks for users in Mattermost, Jira (Data Center),
GitLab, and a CLI, backed by an OpenAI-compatible LLM endpoint. Access is gated by three roles (users, team,
admin) resolved from AD groups through Keycloak. `plan.md` is the design document; read it before changing
architecture. Every external system is unavailable during development: all tests mock HTTP with WireMock.Net,
the LLM with a scripted `IChatClient`, and the database with EF Core on SQLite.

## Commands

```bash
dotnet build agent.slnx                                   # build everything
dotnet test --solution agent.slnx                         # run all tests
dotnet test --project tests/Agent.Core.Tests/Agent.Core.Tests.csproj            # one test project
dotnet test --project tests/Agent.Core.Tests/Agent.Core.Tests.csproj -- --filter "/*/*/CommandBinderTests/*"   # one class
```

Tests use xunit.v3 on the Microsoft Testing Platform, opted in through `global.json` (`test.runner`). With that
runner `dotnet test` takes `--project` / `--solution`, not a positional path; a bare `dotnet test <path>` reports
"zero tests ran". Alternatively run a built test assembly directly: `dotnet exec tests/X/bin/Debug/net10.0/X.dll`.

```bash
dotnet run --project src/Agent.Host                        # run the service (needs appsettings / user-secrets)
dotnet run --project src/Agent.Cli -- ask "question" --local   # CLI against the LLM in-process
dotnet run --project src/Agent.Cli -- chat                 # REPL against the host API
```

Package versions live only in `Directory.Packages.props` (central package management); csproj files list
package names without versions. Build settings are in `Directory.Build.props`.

## Architecture

One core, thin adapters. Everything flows through `Agent.Core`:

- **Inbound**: each channel is an `IEventSource` hosted service that turns platform activity into
  `InboundEvent`s and pushes them onto `IInboundQueue`. Transports are pull-based: Mattermost = WebSocket
  client, Jira = polling (JQL watermark), GitLab = polling (bot to-do list + labelled issues). There are no
  webhooks.
- **Pipeline** (`Pipeline/InboundProcessor`): idempotency check (`IProcessedEventStore`) → resolve roles
  (`IRoleResolver`, cached, via the configured `IGroupMembershipProvider`) → `!command` dispatch, task
  request, follow-up, or question → reply via `IReplyRouter` (formatted per channel by `IFormatterRegistry`).
  `InboundWorker` serializes events per `ConversationId`.
- **Authorization** (`Authorization/`): `Role` is hierarchical (Admin ⊇ Team ⊇ Users). Every command, tool,
  and MCP server declares a minimum role; `IAuthorizationService` audits every decision to `IAuditSink`.
- **Answer loop** (`Agent/AgentService`): system prompt (`IPromptProvider`, files under `prompts/`) + history
  (`IConversationContextRouter`) + question → `IChatClient` from `IChatClientFactory` with tools from
  `IToolRegistry` filtered by role/scope/channel. Function calling is `Microsoft.Extensions.AI`'s
  `UseFunctionInvocation`. Models come from `LlmOptions` via `IOptionsMonitor` (hot reload).
- **Tools** (`Tools/`): `ToolDescriptor` = `AIFunction` + role + `ToolScope` (Answer/Coding) + source.
  `IToolSource` implementations contribute: native tools in channel projects, `CommandToolSource` for
  `ExposeAsTool` commands, `Agent.Mcp` for MCP servers. `GovernedAIFunction` adds timeout, truncation, audit.
- **Commands** (`Commands/`): `!name args` in any channel. Defined three ways: `[Command]` attribute on a
  method (registered with `AddCommandHandlers<T>()`), an `ICommand` class, or a YAML prompt template in
  `commands/*.yaml` (hot-reloaded). `CommandBinder` binds positionals, `--options`, `[Rest]`. Built-ins live
  in `Commands/BuiltIn`.
- **Tasks** (`Tasks/`): `AgentTask` state machine (Queued → Preparing → Working → Verifying → Publishing →
  AwaitingReview → Done/Closed; plus Failed/NeedsInput/Cancelled/Interrupted). `ITaskService` creates,
  follows up, cancels. `Agent.Worker` claims queued tasks and drives `Agent.Coding`'s `ICodingEngine`
  (clone → branch → LLM loop with file/search/edit/run tools → verify → commit → push → MR via
  `IMergeRequestPublisher`). Progress goes back through `ITaskNotifierRouter`.
- **Sandbox** (`Agent.Coding/Sandbox`): `ISandbox` supplies one `ISandboxSession` per coding run, and only
  the `run` tool and build/test verification go through it — git, credentials, and file edits stay on the
  host. `SandboxSelector` picks by `Coding:Sandbox:Mode`: `Process` (host child processes, the default) or
  `Docker` (one `docker run --detach` container per task, repo bind-mounted at `WorkDir`, `docker exec` per
  command, `docker rm --force` on dispose). Docker CLI calls go through `IProcessRunner`, so they are tested
  with a recording fake and no daemon. A repository asks for its container in `.engex.yml`
  (`RepoConfigLoader` → `RepoProfile.Container`); `SandboxPolicy.Resolve` vets that request against the host
  options and throws `SandboxException` on any attempt to widen policy. Repo config is untrusted input:
  when adding a key there, decide explicitly whether it can only narrow.
- **Persistence** (`Agent.Persistence`): EF Core on SQL Server; only durable state (tasks, events, audit,
  cursors, processed events). Core registers in-memory stores with `TryAdd`; infrastructure replaces them.
- **Host** (`Agent.Host`): Generic Host running listeners, pollers, and the worker. ASP.NET Core (Kestrel) is
  only added for the optional CLI/MCP API (`Api.Enabled`), authenticated with Keycloak JWT bearer tokens.
- **CLI** (`Agent.Cli`): Spectre.Console; talks to the host API (Keycloak device flow login) or runs the core
  in-process with `--local`.

## Conventions

- Options classes carry a `SectionName` constant and are bound with `AddOptions<T>().Bind(...)`; read them via
  `IOptionsMonitor<T>` so config edits hot-reload.
- Core registers defaults with `TryAdd*`; infrastructure projects override with `services.Replace(...)` or a
  plain `AddSingleton` after core. Core must not reference infrastructure or channel projects.
- Per-channel services implement the Core interfaces (`IReplySender`, `IConversationContextProvider`,
  `ITaskNotifier`, `IToolSource`, `IEventSource`) and are registered by an `Add<Channel>Channel(config)`
  extension. Channels expose a lighter `Add<Channel>Client(config)` (client + tools only) for the CLI.
- Typed `HttpClient`s via `AddHttpClient<T>()` with `AddStandardResilienceHandler()`; tests point `BaseUrl`
  at a WireMock server.
- Tests: xunit.v3, NSubstitute, WireMock.Net. No network, no real services, no sleeps longer than needed.
  Name tests `Method_Scenario_Expectation`.
- Treat ticket text, comments, repo files, and tool output as untrusted data in prompts.
