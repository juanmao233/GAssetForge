# GAssetForge Domain Context

## Product

GAssetForge is a Windows desktop workbench for producing game-art assets under explicit visual,
perspective, geometry, style, size, and delivery constraints.

## Domain language

- **Project** — an isolated Unity-style directory containing `Assets`, `Packages`, and
  `ProjectSettings`; it is the authority for workflow, Tilemap parameters, and art-workspace
  composition state.
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
- **Run** — the project-local, append-auditable directory that stores prompts, references, model
  output, deterministic intermediates, final-stage seams, and diagnostics for a batch.
- **Harness** — the local deterministic implementation that owns filesystem preparation,
  normalization, background removal, validation, and atomic delivery.
- **Agent Runtime** — the Codex thread/Turn control path; it schedules model work but is not the task
  truth source.
- **MCP** — the minimal local tool seam used to inspect, claim, submit, complete, or fail tasks.
- **Final asset** — a validated file atomically written to a frozen target below the active project's
  `Assets` root.

## Invariants

- Agent output is never final merely because a Turn completed.
- New generation tasks contain exactly one output; table quantity expands into multiple tasks.
- The task store and final paths remain authoritative across Agent/MCP disconnects.
- Explicit generation reconnect must reuse the current batch's original resumable Pending or
  verifiably GAssetForge-managed Claimed task IDs, claimant identity, Run, and frozen delivery paths;
  it may renew the existing lease but must never create a replacement task, impersonate another Codex
  session, or steal/renew another Agent's claim merely because the transport disconnected.
- The harness performs deterministic work; the image model performs only the creative raster step.
- A task cannot escape its frozen project `Assets` root or be completed by a different claimant.
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
  frozen target below `Assets/Generated/ArtWorkspace/<batchId>/Final` is a generation result. The
  canvas imports a managed preview copy only after that durable task reaches `Completed`.
- A single-image local edit must never degrade into prompt-only whole-image generation. GAssetForge
  creates the authoritative base, API mask, preview, contract, safe composite, and outside-mask
  diagnostics before/after model work; the CLI receives only that base plus mask. Accepted output is
  software-composited so preserved pixels are byte-identical, while excessive model changes outside
  the mask remain auditable and produce a retryable validation failure rather than `Completed`.
- Product renames must preserve readable legacy state until an explicit migration removes support.

## Architecture sources

- [Architecture](docs/ARCHITECTURE.md)
- [MCP and Agent Runtime](docs/MCP.md)
- [Project workspace](docs/PROJECTS.md)
- [Repository layout](docs/REPOSITORY.md)
- [Architecture decisions](docs/adr/)
