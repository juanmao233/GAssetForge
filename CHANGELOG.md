# Changelog

All notable changes will be documented in this file. The project currently follows an unreleased
development track; versioning policy will be finalized before the first public release.

## Unreleased

## 1.0.24t1 - 2026-08-28

### Added

- Added an Art Workspace image-region tool. A drag on the selected image stores the marquee in the
  image's unrotated local `0..1` coordinate space, so the same source pixels remain selected while
  the canvas object is moved, resized, or rotated.
- Added an explicit per-instruction image reference to the `art-workspace` generation contract.
  The currently selected, visible managed image is automatically frozen as the user's whole-image
  target, or as the local region when a marquee exists; the generated Prompt no longer asks the
  Agent to guess which canvas image words such as “this image” or “this area” refer to.

### Changed

- Advanced the Art Workspace constraint contract to Schema v2. A local-region reference now carries
  the selected image and asset identities, managed snapshot path, SHA-256-bearing frozen reference,
  normalized bounds, derived source-pixel bounds, and its rotation-aware canvas polygon.
- Kept image-reference details readable but coordinate-free in the conversation UI: users see the
  image name and either “whole image” or “selected local region”, while numeric geometry remains in
  the durable Agent contract and project document.

### Fixed

- Invalid references no longer survive image switching, hiding, deletion, missing managed files, or
  project reload; region selection is available only for a valid visible selected image.
- The local developer publish script now removes a stale `release-info.json` before overwriting
  binaries, preventing a reused `dist` directory from reporting an older version or source provenance.

## 1.0.23t1 - 2026-08-27

### Added

- Connected Art Workspace conversation instructions to the managed Agent/MCP generation harness.
  The Properties panel now reports whether the selected Agent is unavailable, busy, or ready, and
  accepts a single in-flight `发送并生成` request only when a formal project and executable Agent
  session are active.
- Added an `art-workspace` generation contract that freezes the user instruction, output-frame
  geometry and pixel size, project `Assets` root, and every visible canvas reference including its
  SHA-256, position, size, rotation, opacity, and layer order.
- Added live Art Workspace Agent replies and generation progress. A successful final PNG remains in
  the project `Assets/Generated/ArtWorkspace/<batchId>/Final` audit location and is copied into the
  managed Art Workspace reference library for immediate display at the exact frozen output frame.

### Changed

- Art Workspace generation now shares the same durable task store, Run workspace, Codex Runtime,
  MCP tools, and single Agent dispatch coordinator as the material-generation page instead of using
  a presentation-only conversation placeholder.
- Agent transport interruptions preserve the original Art Workspace task and frozen delivery path
  for same-task continuation; they do not create a replacement art correction task.
- Conversation persistence reserves the maximum Agent reply footprint before publication so live
  status updates cannot push a valid workspace beyond its 16 MiB document limit.

### Fixed

- `submit_generation_output` now supports opaque full-frame workflows with no background-removal
  contract and no staging paths. The harness archives the model PNG, creates a deterministic
  normalized input, retains a processed audit copy, validates the frozen output, and atomically
  commits the final image.
- Prevented late generation results from a previous project or an escaped output path from being
  imported into the active Art Workspace canvas.

## 1.0.22t1 - 2026-08-27

### Added

- Added a `重新连接并继续` action beside `刷新状态` in the current-generation-batch panel. It
  re-reads the durable task store and submits only the active project's original resumable
  `Pending` or GAssetForge-managed `Claimed` task IDs from the current batch.

### Changed

- Agent reconnection now force-replaces the Codex Runtime even when the old app-server process still
  reports Ready, while preserving the project working directory and thread bookmark. A healthy MCP
  Server is reused; a stopped, faulted, or mismatched instance is repaired before the Runtime is
  validated as executable again.
- Explicit batch continuation waits for the previous scheduling lifecycle to release, serializes
  against automatic Pending recovery, repairs the existing Run workspace when possible, and then
  idempotently requeues the same batch and task IDs. Pending tasks receive a stable task-derived Codex
  identity, while already Claimed managed tasks reuse and immediately renew their exact frozen
  `ClaimedBy`; completed tasks, unverified Codex/Agent claims, and other historical batches are not
  restarted. Retry lineage, Run directories, and delivery paths are not rewritten.

### Fixed

- Responses/SSE disconnections could leave the Runtime connection flag continuously Ready, so the
  existing false-to-true recovery trigger never ran and the preserved Pending tasks could not be
  woken again from the generation page. The new explicit recovery path reconnects and requeues in one
  bounded operation while preventing duplicate Runtime, MCP, or Agent Turn launches on repeated clicks.
- Faulted transports whose child process remained alive are no longer considered reusable sessions.
  Managed Claimed tasks can now continue immediately with the same owner identity and a refreshed
  lease, while claims belonging to another Codex session or Agent family remain protected and are
  never stolen or renewed by the new Turn.
- MCP processes stuck indefinitely in `Starting` are recycled after the first health timeout and
  restarted exactly once, instead of timing out on every reconnect attempt.

## 1.0.21t1 - 2026-08-27

### Added

- Added intelligent fake-transparency checker detection. The local detector samples all four corners,
  finds the two repeated background colors, counts horizontal and vertical transitions, fits a shared
  floating-point square-cell period and phase, and reports confidence, cell-size range, grid counts,
  contributing corners, and a precise failure reason.
- Added Background Removal Properties controls for per-image smart detection, minimum/maximum cell
  width, corner scan range as a percentage of the source image short side, an explicit "detect current
  image" action, and live detection diagnostics.

### Changed

- Fake-transparency batch removal now detects every image independently and builds an item-local
  checker configuration. Different source scales no longer reuse the first image's cell width, colors,
  or phase, and the detected values never overwrite the user's global manual grid settings.
- A low-confidence or failed detection stops only that batch item and continues later items instead of
  silently applying another image's grid. Manual ruler values remain available when smart detection is
  disabled or after the user adjusts the search range.

### Fixed

- Preserved checker A/B parity when normalizing an inferred offset into one cell period, preventing a
  correctly aligned grid from selecting the opposite background color after an odd-cell phase shift.

## 1.0.20t1 - 2026-08-27

### Added

- Added the central `art-workspace` document for Lovart-style visual composition: users can import or
  drag multiple managed reference images onto a large grid canvas, select and move them, and position
  or resize one explicit output frame for a future generated art asset.
- Added a context-sensitive Art Workspace Properties view with conversation history, a `Ctrl+Enter`
  creation-intent input, output-frame controls, and selected-image transforms, visibility, locking,
  opacity, and layer ordering.
- Added project-scoped Art Workspace persistence under
  `ProjectSettings/ArtWorkspace/art-workspace.v1.json`, managed reference copies under
  `Assets/ArtWorkspace/References`, backup recovery, draft fallback, hash reuse, and path-boundary
  validation. A workspace accepts up to 128 active reference objects and a 16 MiB serialized document,
  while individual imports are constrained by file size, dimensions, and total source pixels.

### Changed

- Registered Art Workspace as an independent AvalonDock document instead of a business template, so
  opening it changes the tool and property context without replacing the current project workflow.
- Made canvas navigation available over both images and the output frame: pointer-anchored wheel zoom,
  middle/right-button panning, right-button double-click reset, and focus-all/output-frame actions.
- Moved managed image import to a cancellable background pipeline with progress feedback, while
  preventing project switches and persistence snapshots from racing an in-flight import.
- Bounded workspace preview decoding by both dimensions and total pixels, with file-version-aware
  cache invalidation and a small shared LRU to keep large or repeated references memory-safe.
- Added a keyboard-accessible image list in Properties so hidden references remain selectable and can
  be shown again; standard `Ctrl+S`, File Save, and the toolbar Save button now target Art Workspace
  when that document is active.
- Bumped the docking preset revision to 13 while keeping the Art Workspace document optional, so users
  can close it and restore it later from the View menu without losing project data.

### Fixed

- Synchronized active business-document identity with workflow activation so Tilemap, card, and
  perspective documents cannot accidentally execute hidden Page UI editor save/import/zoom commands.
- Preserved Blueprint and Art Workspace tool contexts when shared auxiliary panels receive focus, and
  moved the shell to a neutral document state when every central document is closed.
- Recovered from semantically invalid Art Workspace JSON through the valid backup instead of allowing
  null fields, malformed paths, or corrupt image metadata to abort application startup.
- Preserved pending auto-saves across cancelled or failed imports, avoided rewriting corrupt files
  merely by opening a safe-default recovery view, and included rotated image corners in Focus All.

## 1.0.19t1 - 2026-08-26

### Added

- Added an interactive square ruler to the Background Removal source preview for aligning fake-
  transparency checkerboards. Users can draw a reference cell, move it, resize it from any corner,
  and apply the resulting cell size and X/Y phase directly to the processing settings.

### Changed

- Upgraded checker-grid cell size, offsets, and boundary tolerance from integer-only values to
  double-precision source pixels, while retaining compatible reads for settings saved by older versions.
- Kept the ruler and rendered grid aligned to original-image coordinates through viewport zooming and
  panning, and normalized negative or over-period offsets with positive floating-point modulo.

### Fixed

- Removed rounding drift when matching scaled screenshots or other checkerboards with fractional cell
  geometry, and applied boundary tolerance symmetrically on both sides of every cell edge.
- Prevented cancelled or invalid ruler gestures from overwriting the last committed grid alignment.

## 1.0.18t1 - 2026-08-26

### Changed

- Published a packaging-only TEST revision of the fractional checker-grid ruler build. It was
  superseded by `1.0.19t1` without an additional source feature delta.

## 1.0.17t1 - 2026-08-26

### Added

- Added a desktop-wide eyedropper to the shared HSV/RGB color picker. It previews the current screen
  pixel and coordinates, accepts with the left mouse button, and cancels with the right mouse button
  or Escape without changing the pending color.
- Added explicit Per-Monitor-V2 DPI awareness so the native virtual-screen bounds, cursor position,
  and sampled pixel remain in the same coordinate system across scaled and negative-coordinate
  monitors.

### Changed

- Screen sampling now hides the modal picker without ending its dialog session, uses a temporary
  topmost overlay to capture input across applications, and removes that overlay from composition
  before committing the authoritative RGB value.
- Isolated Win32 desktop pixel access behind a testable App service and guaranteed that every screen
  device context is released on success, invalid-color, and exception paths.

## 1.0.16t1 - 2026-08-26

### Added

- Added dedicated Properties-panel workspaces for Background Removal, Image Outline, and Pixel
  Alignment, including file selection, preview publication, output locations, and batch controls.
- Added real Background Removal batch processing with per-item status, progress, isolated failures,
  and deterministic output-path planning alongside the existing outline and pixel batch workflows.
- Added a reusable image viewport module shared by the three image-processing tools, with cursor-
  anchored wheel zoom, middle-button panning, and right-click or double-click reset.

### Changed

- Moved image-tool operations out of the document headers and into the context-sensitive Properties
  panel. Tool documents now use compact headers and devote their workspace to source/result previews.
- Moved Pixel Alignment preview-cache, save-location, single-image, and batch-queue operations into
  its Properties view while retaining its detailed sampling and correction controls in the document.
- Kept fake-transparency checker overlays synchronized with the shared viewport transform so grid
  alignment remains visually accurate while zooming and panning.

## 1.0.15t1 - 2026-08-26

### Added

- Added structured Agent dispatch diagnostics for Turn chunk index/count, task count, elapsed time,
  stable transport failure code, and whether a missing `response.completed` caused interruption.
- Added explicit `Interrupted` generation UI state for upstream Responses stream failures, with the
  original Pending tasks retained for recovery instead of presenting the event as an art failure.

### Changed

- Split large generation batches into serialized Agent Turns of at most five tasks. Every batch starts
  a fresh Codex thread, while all chunks within that batch share the same thread; a failed chunk stops
  later chunks so one upstream disconnect cannot invalidate the whole remaining batch.
- Made task reconciliation follow the persistent MCP/Harness task store exclusively. An Agent Turn
  ending without claiming a task no longer lets the desktop claim and fail that Pending task.
- Restricted automatic retry children to explicit retryable output/delivery failures. Historical
  `agent_turn_incomplete` and `agent_transport_*` records are never expanded into new generation tasks.
- Made Pending recovery edge-triggered on a real Agent disconnected-to-connected transition and gave
  enqueue operations distinct `Enqueued`, `AlreadyScheduled`, and `QueueStopped` outcomes.

### Fixed

- Classified `stream disconnected before completion` and `stream closed before response.completed`
  as `agent_transport_stream_disconnected` instead of local art validation failure.
- Prevented Responses/SSE interruptions from creating “自动重做 2/4 …” task/run trees before image
  generation or MCP claim had occurred.
- Removed the once-per-second repeated Pending recovery scan and the resulting thousands of duplicate
  “已恢复持久化 Pending 批次的 Agent 调度” log entries.

## 1.0.14t1 - 2026-08-26

### Changed

- Background Removal now enables enclosed-background recognition by default for both solid chroma
  keys and aligned fake-transparency checkerboards. The positive UI option maps to the existing
  `edgeConnectedOnly` contract field, so historical generation tasks and MCP payloads remain readable.
- Added an explicit opt-out that restores the previous border-connected-only behavior when a subject
  intentionally contains details that match the selected background color.
- Added a forward user-setting field for the positive option. Settings written by earlier versions
  migrate to the new enabled default, while saves continue synchronizing the legacy inverse field for
  down-level compatibility.

### Fixed

- Removed background colors trapped inside enclosed or hollow subjects instead of leaving opaque
  islands after the outer canvas background was removed.
- Marked cached Background Removal previews stale when the enclosed-region option changes and froze
  the same scope into newly published generation tasks.

## 1.0.13t1 - 2026-08-26

### Added

- Added a reusable Photoshop-style opaque-color picker with a two-dimensional HSV saturation/value
  surface, Hue spectrum, synchronized HSV/RGB/`#RRGGBB` editing, original/new color comparison, and
  cancel-without-commit semantics. Image Outline and all three custom Background Removal colors now
  use the shared service.
- Added a bounded `Workspace/ImageProcessingPreviews` PNG cache with managed-path validation, full PNG
  decode verification, active-preview pinning, unique refresh paths, expiration/capacity cleanup, and
  same-directory atomic publishing to final destinations.

### Changed

- Split single-image Image Outline, Background Removal, and Pixel Alignment into explicit “refresh
  preview” and “save result” stages. A final output path is no longer required to render the real Core
  ImageSharp result, and saving publishes the exact preview bytes without rerunning or re-encoding the
  image operation.
- Kept the last successful preview visible after parameter edits or refresh failures, while marking it
  stale and disabling publication until a new matching render succeeds. Batch outline and pixel
  alignment continue to write each item directly to their selected output directories.
- Injected one shared preview cache and color-picker service from the Shell, while retaining isolated
  temporary fallbacks for standalone ViewModel tests and tooling.

### Fixed

- Prevented parameter edits made during a long preview render from incorrectly marking the completed
  image as current, and kept output-location changes independent from pixel-result validity.
- Prevented opportunistic cache cleanup from removing a preview that is still displayed and available
  for confirmation in another docked image tool.
- Preserved Palette PNG bytes produced by Pixel Alignment when the confirmed preview is saved.

## 1.0.12t1 - 2026-08-26

### Changed

- Made the right-side Properties panel follow the currently active AvalonDock window instead of the
  previously selected business workflow. UI Design, Tilemap, and Icon Design retain their dedicated
  property modules, while Style Library, Asset Generation, Blueprint, project tools, and other windows
  without a property module show a stable context-specific empty state.
- Kept Properties-panel context independent from the Blueprint tool-palette context so selecting a dock
  page changes only the side panel that belongs to that context.
- Made workflow selection focus the corresponding dock document, keeping the selected tab, shell workflow,
  tool palette, and Properties panel synchronized.

### Fixed

- Prevented Style Library and other tool pages from falling back to UI or Tilemap properties after they
  become active.
- Preserved the intended active workflow document after AvalonDock installs or resets the default layout,
  avoiding startup-time selection of an unrelated anchorable as the Properties context.

## 1.0.11t1 - 2026-08-26

### Added

- Added a dedicated Icon properties panel that appears whenever the `icon-design` dock page is active.
  It shares the same settings-backed view model as the dual camera preview and exposes the production mode,
  output canvas, azimuth, elevation, roll, camera distance, focal length, framing diagnostics, presets, and
  generated angle description in the standard right-side Properties workflow.
- Added `Icon 图标制作` as a real Asset Generation step. Published tasks freeze a provider-neutral
  `icon-design` contract containing the exact canvas, camera basis, pinhole projection convention, focal
  length, field of view, normalized framing bounds, transparent-background stages, and reusable angle text.
- Added MCP `get_current_context` support for an independent `activeGenerationStep` and persisted Icon camera
  settings without registering Icon as a New Project template.

### Changed

- Simplified the Icon main dock page to the spatial rig and final camera-frame previews; all editable camera
  and canvas controls now follow the same context-sensitive Properties-panel model as Tilemap.
- Generalized Asset Generation summaries, batch names, project folders, filenames, manifests, and automatic
  or manual retry paths by workflow. Icon outputs now use `Generated/Icon/<batchId>/Final` and `icon_` names,
  while existing Tilemap paths remain unchanged.
- Unified Tilemap and Icon first-stage background contracts through one Core helper while preserving the
  established Tilemap JSON fields and using subject-specific Alpha semantics for Icon assets.

### Fixed

- Prevented switching generation steps or editing Icon settings during asynchronous style snapshot work from
  mutating a batch already being published; the selected step and all perspective parameters are captured
  before the first await and remain immutable in persisted tasks.
- Blocked Icon task publication when the current camera projection cuts the subject outside the frame, with
  an actionable prompt to reduce focal length or increase camera distance.
- Prevented Icon retries and diagnostic MCP context from falling back to Tilemap directories, prefixes, or UI
  workflow constraints.

## 1.0.10t1 - 2026-08-25

### Added

- Added spreadsheet-assisted description entry to Asset Generation step 2. Users can import `.xlsx`,
  `.xls`, `.csv`, or `.tsv`, preview worksheets in an Excel-style grid, select the description column
  and individual valid rows, then append them to or atomically replace the current task table.
- Added bounded spreadsheet parsing through ExcelDataReader without Office/Interop. Formula expressions,
  macros, external links, hyperlinks, and embedded content are never executed; Excel formulas expose only
  cached values already stored in the workbook.
- Added workbook, worksheet, row, column, cell-text, ZIP expansion, shared-string, duplicate-entry, unsafe
  path, and macro-container limits, plus regression coverage for malformed Open XML and BIFF workbooks.

### Changed

- Imported descriptions default to quantity 1 and inherit the currently selected task row's Assets output
  directory. Spreadsheet cells cannot redirect generated files outside the active project.
- Spreadsheet preview state remains session-only, while rows accepted into the task table continue through
  the existing immediate draft persistence path. The page keeps vertical scrolling at 480 px width and
  confines wide worksheet columns to the preview grid's own horizontal scrollbar.

### Fixed

- Preserved user-resized and reordered preview columns when switching the matched description column, while
  still rebuilding columns when the worksheet schema changes.
- Prevented invalid or empty description rows from being selected or counted, normalized late-added columns
  across earlier rows, and made append/replace limits reject the whole import without partial mutation.
- Kept import progress and first-read errors visible inside step 2, wrapped third-party parser failures as
  actionable UI errors, and fixed narrow-layout clipping/overlap in the step header and preview footer.

## 1.0.9t1 - 2026-08-25

### Added

- Added the dockable `Icon 图标制作` workspace. Its first `指定角度生成` mode controls output width and
  height, camera azimuth, elevation, roll, subject distance, and focal length with persistent settings and
  reusable front, three-quarter, and high three-quarter presets.
- Added synchronized dual perspective previews: a left spatial rig view renders the cube, world axes,
  camera body, lens, target line, and view frustum, while the right camera-frame view shows the projected
  cube inside the requested output aspect ratio and warns when the subject leaves the frame.
- Added a deterministic camera projection contract and live generation description so later icon-generation
  workflows can consume the exact angle, focal length, field of view, framing, and image-size constraints.

### Changed

- Registered `icon-design` as a persistent AvalonDock panel, View-menu entry, console `show icon` target,
  layout-migration requirement, and single-instance settings-backed workspace.

## 1.0.8t1 - 2026-08-25

### Added

- Added a dockable Image Outline tool with outside, inside, and centered Alpha-based strokes, configurable
  1–128 px width, arbitrary `#RRGGBB` color, Alpha noise threshold, optional canvas expansion, transparent
  before/after previews, and deterministic atomic PNG output.
- Added Image Outline batch processing for up to 256 images with a shared output directory, per-item status,
  aggregate progress, failure isolation, collision-safe filenames, and immediate preview of the latest result.
- Added fake-transparency checkerboard removal to the Background Removal tool. Users can select two checker
  colors and optionally align a square grid by cell size, X/Y offset, and boundary tolerance while a live
  overlay shows the expected grid over the source image.

### Changed

- Background removal now supports per-pixel checkerboard key selection and despill while preserving the old
  single-color path. The AI generation contract still uses the stable solid-color first stage; checkerboard
  recovery remains an explicit local fallback for rasterized fake transparency.
- Registered the Image Outline tool as a persistent AvalonDock panel, View menu item, console `show outline`
  target, and user-settings-backed single-instance tool page.

## 1.0.7t1 - 2026-08-25

### Changed

- Reworked Asset Generation into one ordered vertical flow: generation setup, task table, publishing
  controls, current batch diagnostics, Agent feedback, manual recovery, and generated artifacts now
  appear sequentially from top to bottom instead of being split across left and right columns.
- Removed the page-level horizontal splitter and bottom horizontal scrollbar. The dock page now supports
  a 480 px minimum width and uses vertical scrolling for compact layouts, while wide task tables retain
  their own local horizontal scrolling where necessary.

## 1.0.6t1 - 2026-08-25

### Added

- Added automatic indexed-color output to Pixel Alignment. It builds a per-image Wu palette after
  geometry correction and cell sampling, defaults to 256 colors, supports a configurable 2–256 color
  limit, disables dithering for pixel-art stability, and writes a real palette PNG while retaining
  per-entry Alpha.
- Added Pixel Alignment batch processing with multi-file selection, a dedicated output directory,
  per-item status, aggregate progress, and sequential processing that continues after individual
  failures.

### Changed

- Batch processing freezes the reference image's crop rulers, perspective points, correction settings,
  sampling mode, and color-index settings, then proportionally maps source-space coordinates onto each
  differently sized image.
- Batch outputs never silently overwrite an existing file. Name collisions receive a deterministic
  numeric suffix, and the latest successful output is immediately available in the preview.

### Fixed

- Fully transparent pixels are normalized to transparent black before automatic palette construction,
  preventing invisible RGB values from consuming multiple color entries. Disabling color indexing keeps
  the original RGBA path unchanged.

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
