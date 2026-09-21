using System.Collections.ObjectModel;
using System.Text.Json;
using GAssetForge.Core.Generation;

namespace GAssetForge.App.Services;

public static class GenerationMcpToolPolicy
{
    public const string DefaultToolsApprovalMode = "approve";
    public const int ToolTimeoutSeconds = GenerationMcpRuntimePolicy.ToolTimeoutSeconds;

    public static ReadOnlyCollection<string> EnabledTools { get; } = Array.AsReadOnly(
    [
        "server_info",
        "get_generation_task",
        "claim_generation_task",
        "submit_generation_output",
        "run_image_delivery_fallback",
        "process_generation_output",
        "complete_generation_task",
        "fail_generation_task"
    ]);

    public static string BuildTomlToolArray() =>
        $"[{string.Join(", ", EnabledTools.Select(toolName => JsonSerializer.Serialize(toolName)))}]";
}
