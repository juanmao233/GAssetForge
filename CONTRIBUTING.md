# Contributing to GAssetForge

GAssetForge is a Windows-first WPF workbench. Contributions should preserve deterministic local
processing, project isolation, and the durable generation-task contract.

## Prerequisites

- Windows 10 or later
- .NET 8 SDK (the repository `global.json` allows later .NET 8 feature bands)
- PowerShell 7 or Windows PowerShell 5.1
- Visual Studio 2022 is optional; the CLI commands below are authoritative

## Build and verify

```powershell
dotnet restore .\GAssetForge.sln --locked-mode
dotnet build .\GAssetForge.sln -c Release --no-restore
dotnet run --project .\tests\GAssetForge.SmokeTests\GAssetForge.SmokeTests.csproj -c Release --no-build
```

Update `packages.lock.json` only when intentionally changing dependencies:

```powershell
dotnet restore .\GAssetForge.sln --force-evaluate
```

## Change guidelines

- Keep WPF UI orchestration in `GAssetForge.App`, deterministic contracts and image processing in
  `GAssetForge.Core`, and Agent-facing transport/tools in `GAssetForge.McpServer`.
- Do not make the Agent create project directories, move outputs, resize images, remove backgrounds,
  or decide final paths when the local harness can perform that work deterministically.
- Treat project manifests, task JSON, MCP identifiers, LocalAppData paths, and generated Run paths as
  compatibility contracts. Add migration behavior and smoke coverage before changing them.
- Never commit generated artwork, user projects, prompts, tokens, logs, `dist/`, `artifacts/`, `bin/`,
  or `obj/`.
- Tests must not call paid image-generation APIs or start a real Agent Turn.

## Pull requests

Keep each pull request focused on one workflow outcome. Describe the user-visible result, tests run,
and any migration implications. Security issues must follow [SECURITY.md](SECURITY.md), not a public
issue.

## License status

The repository owner has not yet selected the project license. A public repository is not legally
open source until a project `LICENSE` is added. Contributions should not be solicited before that
choice and the contribution terms are published.
