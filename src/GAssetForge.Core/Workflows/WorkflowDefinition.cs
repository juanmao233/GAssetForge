namespace GAssetForge.Core.Workflows;

public sealed record WorkflowDefinition(
    WorkflowKind Kind,
    string Key,
    string Label,
    string Category,
    string Description,
    string Accent,
    bool IsImplemented);
