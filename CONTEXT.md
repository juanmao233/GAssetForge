# GAssetForge Domain Context

## Product

GAssetForge is a Windows desktop workbench for producing game-art assets under explicit visual,
perspective, geometry, style, size, and delivery constraints.

## Open-source target

GAssetForge is a personal learning project shared under the MIT license. Its editor source,
extension contracts, build instructions, issue process, and contribution workflow are public.
Local editing and image processing do not require a model provider. Optional AI workflows depend
on separately configured Agent and image-model services. Third-party components retain their own
licenses; see `LICENSE` and `THIRD-PARTY-NOTICES.md`.

## Domain language

- **Project** — an isolated Unity-style directory containing `Assets`, `Packages`, and
  `ProjectSettings`; it is the authority for workflow, Tilemap parameters, and art-workspace
  composition state.
- **Project asset layout** — the `ProjectAssetLayout` Module and Interface that own every canonical
  project-relative asset path. It divides `Assets` into `AI` Run workspaces, validated `Final`
  deliveries, and user-created or imported `User` content; ViewModels must request paths from this
  Module instead of composing directory names locally.
- **AI workspace** — a category-specific, append-auditable Run below
  `Assets/AI/<ArtWorkspace|Tilemap|Icon>/<batchId>` (or `Assets/AI/Blueprint/<runId>`), containing
  prompts, managed references, model output, deterministic intermediates, and diagnostics.
- **Final asset collection** — the project-local validated delivery library below
  `Assets/Final/[optional collection]/<category>/<batchId>`. Only Harness-validated targets are
  authoritative final assets.
- **User content** — project assets created or imported by the user below `Assets/User`, including
  managed Art Workspace references below `Assets/User/ArtWorkspace/References`.
- **Workflow** — a business template such as Tilemap, card, parallel-perspective object, UI design,
  or test. A workflow selects the main dock document and its constraint module.
- **Art creation workspace** — the cross-workflow central `LayoutDocument` identified by
  `art-workspace`. It is not a `WorkflowKind`: its large canvas composes managed reference images
  around an output frame, while its Properties module captures the user's creation intent.
- **Inline instruction image token** — an image reference inserted at an exact cursor position in an
  Art Workspace instruction. Each token independently freezes one managed visible image and either
  its whole-image state or insertion-time image-local normalized region, preserving ordered A/B/C
  semantics without exposing IDs, paths, hashes, or coordinates in the conversation UI.
- **Region-edit contract** — the pixel-authoritative operation created when an Art Workspace
  instruction contains one frozen regional image token. It names one current base image, excludes
  history images, freezes an Alpha mask and hashes, and requires the Harness to preserve every
  pixel outside the mask.
- **Blueprint workspace** — the dockable, project-persisted graph editor for describing Agent,
  Harness tool, condition, and function-collection execution trajectories. Editing graphs are
  validated and frozen into versioned execution plans before a run is published.
- **Style library** — reusable groups of reference images plus human and AI-oriented style
  descriptions.
- **Image processing workspace** — a dock document for deterministic raster inspection and editing;
  its canvas owns previews while the shared Properties panel owns file, output, publication, and
  batch operations for the active tool.
- **Image viewport** — the reusable preview-navigation contract shared by image tools: wheel zooms
  around the pointer, middle-button drag pans, and right-click or double-click resets the view.
- **Generation task** — one durable, single-image unit with frozen constraints, ownership, lease,
  status, workspace, and final target.
- **Batch** — an ordered group of generation tasks expanded from the generation table.
- **Run** — the category-specific project-local AI workspace that stores prompts, references, model
  output, deterministic intermediates, final-stage seams, and diagnostics for a batch.
- **Harness** — the local deterministic implementation that owns filesystem preparation,
  normalization, background removal, validation, and atomic delivery.
- **Agent Runtime** — the Codex thread/Turn control path; it schedules model work but is not the task
  truth source.
- **MCP** — the minimal local tool seam used to inspect, claim, submit, complete, or fail tasks.
- **Final asset** — for new work, a validated file atomically written to a frozen target below the
  active project's `Assets/Final` root; a legacy task's already-frozen target remains authoritative.

## Invariants

- Agent output is never final merely because a Turn completed.
- Thread creation is not execution readiness. Each managed generation thread owns an MCP readiness
  cycle; old-thread notifications, completed wait signals, and canceled or stopping initialization
  cannot authorize a new Turn. Only explicit start/resume responses select the managed thread.
- Dispatch diagnostics distinguish preparation, submission, confirmed Turn execution, and durable
  task failure. Capture occurrence time and readiness at the failing boundary, separately from log
  write time, and carry batch/task/thread identifiers through the UI and redacted JSONL logs.
- Agent Turn completion must retain any persisted image-task failure in its final summary. MCP
  readiness and local output preflight do not prove image-service network reachability or API access.
- Child-process network settings preserve explicit proxy environment overrides; otherwise Windows
  manual proxy settings may be translated without changing registry/global configuration. The
  authenticated loopback MCP endpoint must bypass external proxies; diagnostic logs never print
  proxy credentials or API keys.
- New generation tasks contain exactly one output; table quantity expands into multiple tasks.
- The task store and final paths remain authoritative across Agent/MCP disconnects.
- An Art Workspace UI waiter is not durable task ownership. A queue stop or transport interruption
  must release the conversation send gate while retaining the original task, batch owner, Run, and
  frozen target; a later same-session terminal notification may update and import the original
  request exactly once.
- Restart recovery never auto-dispatches an Art Workspace batch without a live conversation owner.
  Clearing art chat invalidates its UI requests, drains initial/retry publication, persistently
  withdraws automatic dispatch, then cancels only that project's art batches (including an already
  completed task whose provider Turn has not ended). It preserves claim ownership, frozen files,
  canvas images, and the draft; canceled callbacks cannot mutate the next conversation.
- Explicit generation reconnect must reuse the current batch's original resumable Pending or
  verifiably GAssetForge-managed Claimed task IDs, claimant identity, Run, and frozen delivery paths;
  it may renew the existing lease but must never create a replacement task, impersonate another Codex
  session, or steal/renew another Agent's claim merely because the transport disconnected.
- The harness performs deterministic work; the image model performs only the creative raster step.
- A task cannot escape its frozen project `Assets` root or be completed by a different claimant.
- New project writes use only the three canonical roots owned by `ProjectAssetLayout`: Run work goes
  to `Assets/AI`, validated deliveries go to `Assets/Final`, and user-created or imported content goes
  to `Assets/User`. Feature ViewModels must not duplicate path composition or reinterpret categories.
- `90°` control angle plus `0°` rotation means an axis-aligned square, not a 45° diamond.
- Activating an image-processing dock page must switch the shared Properties panel to the matching
  operation module without duplicating those controls in the document header.
- `ArtWorkspaceView` and `ArtWorkspacePropertiesView` must share one project-bound ViewModel; dock
  layout XML stores only shell placement, never canvas images, output geometry, or conversation.
- Art Workspace imports are managed, cancellable copies; active references are capped and every
  hidden image must remain recoverable from the Properties image list.
- An Art Workspace local marquee is stored against the selected image in its unrotated normalized
  `0..1` coordinate space. Moving, scaling, or rotating the canvas object must not change the source
  pixels it denotes. Inserting a token freezes the current marquee independently; later selection or
  marquee changes must not rewrite it, and a hidden, deleted, changed, or missing source must make
  that token invalid and block publication rather than retargeting it.
- An art-workspace instruction may start generation only when a formal project and an executable
  managed Agent connection are active. Publication freezes the intent, output frame, project
  `Assets` root, and visible reference hashes/transforms into a durable `art-workspace` task. Ordered
  text/image segments preserve every inline token's insertion position and local responsibility; each
  token freezes its image/asset identity, managed snapshot and SHA-256, and a local region additionally
  freezes normalized bounds, source-pixel bounds, and the rotation-aware canvas polygon. Existing
  plain-text conversation JSON and legacy single `InstructionReference` requests remain readable,
  while new publications use the ordered segment contract and coordinate-free user-visible chips.
- Art Workspace Agent progress is advisory; only a validated PNG atomically delivered to the exact
  frozen target below `Assets/Final/[optional collection]/ArtWorkspace/<batchId>` is a generation result. The
  canvas imports a managed preview copy only after that durable task reaches `Completed`.
- A single-image local edit must never degrade into prompt-only whole-image generation. GAssetForge
  creates the authoritative base, API mask, preview, contract, safe composite, and outside-mask
  diagnostics before/after model work; the CLI receives only that base plus mask. Accepted output is
  software-composited so preserved pixels are byte-identical, while excessive model changes outside
  the mask remain auditable and produce a retryable validation failure rather than `Completed`.
- Existing `Assets/Generated`, `Assets/ArtWorkspace/References`, and frozen task delivery/workspace
  paths remain readable and repairable in place. They are never bulk-moved or silently rewritten to
  the canonical layout; new work uses `ProjectAssetLayout` paths.
- Product renames must preserve readable legacy state until an explicit migration removes support.

## Architecture sources

- [Architecture](docs/ARCHITECTURE.md)
- [MCP and Agent Runtime](docs/MCP.md)
- [Project workspace](docs/PROJECTS.md)
- [Repository layout](docs/REPOSITORY.md)
- [Architecture decisions](docs/adr/)
