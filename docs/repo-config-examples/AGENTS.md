# AGENTS.md (example — copy to the root of a repository the agent works on)

Guidance the coding agent reads before touching this repository. Keep it short and concrete.

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
