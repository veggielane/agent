# AGENTS.md (example — copy to the root of a repository the agent works on)

Standing context about how this repository works, read before every task. Keep it short and concrete.

This is not the place to describe work you want done: the task comes from the ticket or the mention that
starts it. Write down only what would otherwise have to be rediscovered, or repeated in every ticket.
Structured settings (build and test commands, container, protected paths) go in `.engex.yml` instead.

## Build and test

- Build: `dotnet build -warnaserror`
- Tests: `dotnet test --no-build`
- Format: `dotnet format --verify-no-changes`

## Conventions

- File-scoped namespaces, `sealed` classes by default, records for DTOs.
- Public APIs need XML doc comments; internal code does not.
- Tests live next to the code they cover under `tests/<Project>.Tests`, one class per production class.
- Never edit files under `deploy/` or `.gitlab-ci.yml`; open an issue instead.

## Things that look wrong but are intentional

- `Legacy/` is a vendored copy of an old library; do not refactor it.
