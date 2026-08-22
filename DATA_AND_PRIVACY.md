# Data and Privacy

GAssetForge is a local desktop workbench, but an AI generation workflow can send selected content to
the configured Agent and image model.

## Stored locally

- user settings, docking layout, style-library metadata, task queue, logs, and Agent session bookmark;
- project manifests and UI project data;
- frozen style-reference snapshots, prompts, model outputs, normalized images, background-removed
  intermediates, final assets, and diagnostic timelines.

Fresh installations use `%LocalAppData%\GAssetForge\Desktop`. If only the old
`%LocalAppData%\PerspectiveForge\Desktop` root exists, the application continues using it so an
upgrade does not make existing state appear lost.

## Potentially sent to an Agent/model provider

When the user starts generation, the task prompt, generation constraints, selected style references,
and model input needed for the task may be processed by Codex/OpenAI or another configured provider.
Provider retention, training, and account policies are controlled by that provider and deployment.

## Local-only deterministic processing

Project directory creation, task persistence, intermediate-file classification, resizing and
normalization, chroma-key background removal, pixel alignment, validation, and final atomic commit are
implemented locally. The repository code does not add an independent analytics or telemetry client.

## Cleanup

Before sharing logs or a project, inspect `Assets\Generated\GAssetForge\Runs`, project manifests,
prompt files, reference snapshots, and `%LocalAppData%\GAssetForge\Desktop\Logs`. Remove content you
do not intend to disclose. Do not delete an active Run while its tasks are pending or claimed.
