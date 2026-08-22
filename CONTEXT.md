# GAssetForge Domain Context

## Product

GAssetForge is a Windows desktop workbench for producing game-art assets under explicit visual,
perspective, geometry, style, size, and delivery constraints.

## Domain language

- **Project** — an isolated Unity-style directory containing `Assets`, `Packages`, and
  `ProjectSettings`; it is the authority for workflow and Tilemap parameters.
- **Workflow** — a business template such as Tilemap, card, parallel-perspective object, UI design,
  or test. A workflow selects the main dock document and its constraint module.
- **Blueprint workspace** — the dockable graph-editor shell for describing Agent and tool execution
  trajectories. The first revision intentionally has no executable graph or persisted node model.
- **Style library** — reusable groups of reference images plus human and AI-oriented style
  descriptions.
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
- The harness performs deterministic work; the image model performs only the creative raster step.
- A task cannot escape its frozen project `Assets` root or be completed by a different claimant.
- `90°` control angle plus `0°` rotation means an axis-aligned square, not a 45° diamond.
- Product renames must preserve readable legacy state until an explicit migration removes support.

## Architecture sources

- [Architecture](docs/ARCHITECTURE.md)
- [MCP and Agent Runtime](docs/MCP.md)
- [Project workspace](docs/PROJECTS.md)
- [Repository layout](docs/REPOSITORY.md)
- [Architecture decisions](docs/adr/)
