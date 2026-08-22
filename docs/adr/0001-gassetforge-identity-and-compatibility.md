# ADR-0001: Adopt GAssetForge identity with legacy reads

- Status: Accepted
- Date: 2026-08-22

## Context

The original working name appeared in assemblies, namespaces, executables, LocalAppData, environment
variables, MCP configuration, project manifests, generated Run paths, staging paths, and durable task
ownership. A blind rename would make existing state appear lost and could strand claimed tasks.

## Decision

Use `GAssetForge` for the solution, namespaces, assemblies, executables, UI, documentation, new data,
MCP configuration, project manifests, Run paths, and staging paths.

Keep legacy identifiers as read-only compatibility aliases centralized in
`GAssetForge.Core.ProductIdentity`:

- use the old LocalAppData root only when the canonical root does not exist;
- prefer new environment variables and fall back to old data-root variables;
- accept the old MCP server name in status and elicitation handling;
- discover old project manifest files and preserve their path when saving;
- accept frozen old staging paths and dispatch reconciler ownership;
- never bulk-rewrite historical task JSON, manifests, logs, or generated Runs.

## Consequences

Fresh work is branded consistently while existing projects and queues continue to work. Compatibility
branches add a small maintenance cost and require regression tests. Removing an alias later requires a
versioned migration and a new ADR.
