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
- Capturing an art-workspace instruction currently persists creation intent only. It must not be
  reported as AI generation or as a final asset until a future Agent/Harness path freezes the intent,
  produces a real image, validates it, and atomically delivers it below the active project's
  `Assets` root.
- Product renames must preserve readable legacy state until an explicit migration removes support.

## Architecture sources

- [Architecture](docs/ARCHITECTURE.md)
- [MCP and Agent Runtime](docs/MCP.md)
- [Project workspace](docs/PROJECTS.md)
- [Repository layout](docs/REPOSITORY.md)
- [Architecture decisions](docs/adr/)
