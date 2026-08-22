# Repository Layout

```text
GAssetForge.sln
src/
  GAssetForge.App/          WPF shell, docking UI, view models, desktop adapters
  GAssetForge.Core/         project/task contracts, validators, geometry, local image processing
  GAssetForge.McpServer/    loopback HTTP/stdio MCP transport and Agent-facing tools
tests/
  GAssetForge.SmokeTests/   isolated STA WPF and protocol smoke runner
docs/                       architecture, projects, MCP, migration, ADRs
scripts/                    Windows publish entry points
.github/                    CI, dependency updates, issue and PR templates
licenses/                   verbatim third-party license texts
```

`dist/`, `artifacts/`, `bin/`, and `obj/` are generated and must remain outside Git history. The old
HTML prototype and local generated images are intentionally outside this repository.

## Dependency direction

```text
GAssetForge.App ───────┐
                      ├──> GAssetForge.Core
GAssetForge.McpServer ─┘

GAssetForge.SmokeTests -> App + Core + McpServer
```

Core must not reference WPF or the MCP executable. App and MCP may share contracts through Core but
must not silently duplicate geometry, validation, or generation-task rules.
