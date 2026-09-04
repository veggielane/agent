# MCP server definitions

Drop one JSON file per Model Context Protocol server into this directory. Files are hot-reloaded and
override entries with the same name under `Mcp:Servers` in appsettings.

```jsonc
{
  "Name": "docs",
  "Enabled": true,
  "Transport": "Stdio",              // Stdio | Http
  "Command": "npx",
  "Args": ["-y", "@team/docs-mcp"],
  "Env": { "DOCS_ROOT": "D:\\docs" },
  "Role": "Users",                    // Users | Team | Admin — minimum role that may use the tools
  "Scope": ["Answer", "Coding"],      // which loops receive the tools
  "Tools": { "Allow": ["search*", "read*"], "Deny": [], "Roles": { "read_private*": "Team" } },
  "TimeoutSeconds": 30
}
```

For HTTP servers use `"Transport": "Http"`, `"Url": "https://mcp.internal/inventory"` and optional `"Headers"`.
Secrets in `Headers`/`Env` should come from environment variables or user-secrets, not from files checked in.
