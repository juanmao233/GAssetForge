# Repository Layout

```text
GAssetForge.sln
src/
  GAssetForge.App/          WPF shell, docking UI, view models, desktop adapters
  GAssetForge.Core/         project/task contracts, validators, geometry, local image processing
  GAssetForge.EngineeringLauncher/ generated root EXE source; builds current App/MCP before launch
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

`scripts/build-engineering-entry.ps1` publishes the small framework-dependent engineering launcher
to the ignored root `GAssetForge.exe`. That launcher never reads `dist`: it incrementally builds the
current App and MCP projects and starts the Debug apphost only when both builds succeed.

## Dependency direction

```text
GAssetForge.App ───────┐
                      ├──> GAssetForge.Core
GAssetForge.McpServer ─┘

GAssetForge.EngineeringLauncher --process boundary--> current App + MCP Debug builds

GAssetForge.SmokeTests -> App + Core + McpServer + EngineeringLauncher
```

Core must not reference WPF or the MCP executable. App and MCP may share contracts through Core but
must not silently duplicate geometry, validation, or generation-task rules.

## Workspace boundaries

This checkout is the product source of truth and must build without sibling workspaces.
Historical prototypes, private release tooling and local generated assets are not part of the
repository and are not required to build or run it. Use the checked-in `scripts/` for public build
and packaging instructions. Code rollback uses Git; generated artifacts stay outside Git history.
