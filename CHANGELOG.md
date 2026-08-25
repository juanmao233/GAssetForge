# Changelog

All notable changes will be documented in this file. The project currently follows an unreleased
development track; versioning policy will be finalized before the first public release.

## Unreleased

## 1.0.5t1 - 2026-08-25

### Added

- Added a reusable unified prompt sample to each art style group, preserving the manually authored
  style prompt separately and recording the generated prompt, summary, source fingerprint, analysis
  time, analyzer identity/version, request ID, and the complete analyzed asset set.
- Added an "Analyze unified prompt" action to the Art Style Library. It submits one bounded Agent Turn
  that must inspect every managed reference image and returns a structured result without generating
  or editing artwork.
- Added a one-shot workspace protocol at
  `.gassetforge/style-analysis/<requestId>/{request.v1.json,result.v1.json,diagnostics/}`. The desktop
  writes the frozen request and remains the only component allowed to atomically update the style
  library after validating the Agent result.

### Changed

- Asset Generation now enables unified-prompt priority by default. A fresh sample is frozen as
  `sourceMode=unified-prompt-only` and deliberately omits reference images, avoiding repeated visual
  analysis; missing, stale, or explicitly disabled samples fall back to the existing description,
  manual prompt, and immutable reference-snapshot path.
- Editing the style description, manual prompt, asset membership, asset hash, or media type now marks
  an existing unified sample stale without deleting it, so users retain the previous result for review
  while new generation work cannot mistake it for the current style definition.

### Security

- Treat reference images, embedded text, QR codes, metadata, and filenames as untrusted analysis data.
  The result reader rejects reparse-point/path escapes, invalid or BOM-prefixed UTF-8, extra or missing
  JSON fields, identity/fingerprint mismatches, changed reference hashes, invalid timestamps, and
  over-limit text before persisting a sample.

## 1.0.4t1 - 2026-08-25

### Added

- Added configurable automatic recovery for retryable generation failures. Automatic retry is enabled
  by default with three redo attempts after the initial generation, persists a frozen attempt lineage,
  and creates a new task, batch, Run, prompt, and delivery path for every attempt without overwriting
  prior artifacts.
- Added live four-stage artifact visibility for model output, normalized input, background-removed
  output, and final assets. Intermediate files now appear as soon as they land and use distinct
  processing, success, retry-warning, and manual-intervention states without inflating final progress.
- Added structured Tilemap foreground diagnostics covering the bounding box, actual and expected aspect
  ratios, corner occupancy, estimated rotation, diamond score, and validated image path.

### Changed

- `submit_generation_output` now treats recoverable Tilemap geometry failures as a successful warning
  result with `outcome=retryable_validation_failure`, preserves imported/intermediate files, and records
  the attempt as retryable instead of returning an MCP tool error or misreporting image delivery failure.
- Automatic retry now runs after the Agent Turn is reconciled, atomically supersedes each failed attempt,
  injects its measured geometry into the next prompt, and stops at the frozen limit or when a failure
  requires manual intervention.
- Persisted dispatch intent now lets the desktop recover the same Pending batch after restart, project
  switching, or Agent reconnection. Recovery repairs missing Run directories, prompts, and manifests,
  while process-local batch deduplication prevents duplicate Agent Turns.
- Automatic retry preparation failures now persist an explicit blocked reason and expose manual takeover
  instead of leaving the task indefinitely labelled as "automatic retry in progress".

### Fixed

- Split axis-aligned Tilemap validation into `tilemap_geometry_aspect_ratio_mismatch`,
  `tilemap_geometry_rotation_mismatch`, and `tilemap_geometry_diamond_detected` under the outer
  `output_geometry_validation_failed` code, so a tall axis-aligned rectangle is no longer classified as
  a rotated diamond solely because its corners are not fully occupied.

## 1.0.3t1 - 2026-08-25

### Fixed

- Treat a successful Codex `imageGeneration` item with `savedPath=null` as an explicit local-delivery
  gap, then let the GAssetForge Harness run the bundled official `image_gen.py` against the frozen
  task contract and exact project-local model output path.
- Refresh the managed Codex Runtime environment before every Turn and rebuild the Runtime while
  preserving its thread when PATH, Python, Image API, proxy, certificate, or Codex Home settings
  change.
- Discover Python from an explicit `GASSETFORGE_PYTHON_EXECUTABLE`, current LocalAppData installs,
  the latest User/Machine PATH, or `py -3`, with semantic Python version ordering and UTF-8 process
  I/O.
- Serialize concurrent fallback requests per frozen output path, quarantine corrupt or partially
  written PNG files for diagnosis, and make completed-task retries idempotent.
- Extend the managed Codex MCP tool timeout beyond the bounded Image API generation window and stop
  waiting on stdout/stderr pipes when a terminated child process leaves inherited handles open.
- Report image-generation `savedPath` state separately from the potentially large visible result and
  keep API keys, proxy credentials, and authenticated base URLs out of diagnostics.

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
