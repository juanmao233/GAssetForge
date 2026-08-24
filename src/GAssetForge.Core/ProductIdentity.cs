using System.Reflection;

namespace GAssetForge.Core;

/// <summary>
/// Canonical product identifiers plus legacy aliases that remain readable during the
/// PerspectiveForge to GAssetForge transition.
/// </summary>
public static class ProductIdentity
{
    public const string ProductName = "GAssetForge";
    public const string LegacyProductName = "PerspectiveForge";

    public const string DataDirectoryName = "GAssetForge";
    public const string LegacyDataDirectoryName = "PerspectiveForge";
    public const string DataRootEnvironmentVariableName = "GASSETFORGE_DATA_ROOT";
    public const string LegacyDataRootEnvironmentVariableName = "PERSPECTIVEFORGE_DATA_ROOT";
    public const string BearerTokenEnvironmentVariableName = "GASSETFORGE_MCP_BEARER_TOKEN";
    public const string LegacyBearerTokenEnvironmentVariableName = "PERSPECTIVEFORGE_MCP_BEARER_TOKEN";

    public const string McpServerName = "gasset_forge";
    public const string LegacyMcpServerName = "perspective_forge";
    public const string OpenCodeMcpServerName = "gasset-forge";
    public const string LegacyOpenCodeMcpServerName = "perspective-forge";

    public const string ProjectManifestFileName = "gassetforge.project.json";
    public const string LegacyProjectManifestFileName = "perspective-forge.project.json";
    public const string GenerationRunContainerRelativePath = "Generated/GAssetForge/Runs";
    public const string LegacyGenerationRunContainerRelativePath = "Generated/PerspectiveForge/Runs";
    public const string BackgroundStagingDirectoryName = ".gassetforge-staging";
    public const string LegacyBackgroundStagingDirectoryName = ".perspectiveforge-staging";

    public const string DispatchReconcilerAgentId = "gassetforge:dispatch-reconciler";
    public const string LegacyDispatchReconcilerAgentId = "perspective-forge:dispatch-reconciler";

    public static string InformationalVersion { get; } =
        GetAssemblyInformationalVersion(typeof(ProductIdentity).Assembly);

    public static string GetAssemblyInformationalVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? throw new InvalidOperationException(
                $"程序集 {assembly.GetName().Name} 缺少 AssemblyInformationalVersion。");
    }

    public static bool IsKnownMcpServerName(string? value) =>
        string.Equals(value, McpServerName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, LegacyMcpServerName, StringComparison.OrdinalIgnoreCase);

    public static bool IsDispatchReconcilerAgent(string? value) =>
        string.Equals(value, DispatchReconcilerAgentId, StringComparison.Ordinal) ||
        string.Equals(value, LegacyDispatchReconcilerAgentId, StringComparison.Ordinal);
}
