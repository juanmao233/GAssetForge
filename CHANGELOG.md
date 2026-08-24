# Changelog

All notable changes will be documented in this file. The project currently follows an unreleased
development track; versioning policy will be finalized before the first public release.

## Unreleased

## 1.0.2t1 - 2026-08-24

### Fixed

- Decode Codex stdin, stdout, and stderr as UTF-8 without a BOM, safely frame concatenated or
  partially malformed JSONL, and keep non-protocol output out of the Agent status message.
- Keep the managed MCP server independent from Codex Runtime startup and preserve actionable
  diagnostics for every Runtime candidate, including inaccessible WindowsApps executables.
- Require an explicit, readable local PNG `sourcePath` before claiming or completing generation
  work; preserve recoverable model output and quarantine corrupt files without overwriting them.
- Keep historical tasks claimable when they do not contain the new project Run workspace contract,
  while rejecting style references and output paths that cross junction or symbolic-link boundaries.
- Freeze the per-Turn workspace-write sandbox policy with project-only writable roots and outbound
  network access for the official image-generation fallback.

### Changed

- Added structured image-generation contracts for prompt, style references, target dimensions, and
  required perspective rules, including the axis-aligned Tilemap 0-degree invariant.
- Image delivery preflight now executes `image_gen.py --help` with the detected Python Runtime rather
  than treating file existence and `python --version` as proof that the fallback CLI is runnable,
  and separately verifies that the required OpenAI SDK can be imported.

## 1.0.0t1 - 2026-08-22

### Changed

- Renamed the product, solution, assemblies, namespaces, executables, MCP configuration, and publish
  output from PerspectiveForge to GAssetForge.
- Added compatibility reads for the legacy LocalAppData root, data-root environment variable, MCP
  server alias, project manifest, generation staging directory, and dispatch reconciler identity.
- Added repository hygiene, dependency lock files, Windows CI, contribution/security documentation,
  third-party notices, and GitHub templates.
