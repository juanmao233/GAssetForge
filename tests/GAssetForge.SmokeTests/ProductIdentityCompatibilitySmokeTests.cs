using GAssetForge.App.Services;
using GAssetForge.Core;
using GAssetForge.McpServer;
using System.IO;

namespace GAssetForge.SmokeTests;

internal static class ProductIdentityCompatibilitySmokeTests
{
    public static void Run()
    {
        LegacyMcpAndDispatchAliasesRemainRecognized();
        DefaultWorkspacePrefersExistingLegacyDataUntilCanonicalDataExists();
        ProjectPathsOpenLegacyManifestAndPreferCanonicalManifestWhenPresent();
        McpServerAcceptsLegacyDataRootEnvironmentVariableWithCanonicalPrecedence();
    }

    private static void LegacyMcpAndDispatchAliasesRemainRecognized()
    {
        True(ProductIdentity.IsKnownMcpServerName(ProductIdentity.McpServerName),
            "新 MCP 名称必须可识别");
        True(ProductIdentity.IsKnownMcpServerName(ProductIdentity.LegacyMcpServerName),
            "旧 MCP 名称必须在迁移期可识别");
        True(ProductIdentity.IsDispatchReconcilerAgent(ProductIdentity.DispatchReconcilerAgentId),
            "新调度代理身份必须可识别");
        True(ProductIdentity.IsDispatchReconcilerAgent(ProductIdentity.LegacyDispatchReconcilerAgentId),
            "旧调度代理身份必须在恢复未完成任务时可识别");
    }

    private static void DefaultWorkspacePrefersExistingLegacyDataUntilCanonicalDataExists()
    {
        var root = CreateRoot("workspace-root");
        var legacyRoot = Path.Combine(root, ProductIdentity.LegacyDataDirectoryName, "Desktop");
        var canonicalRoot = Path.Combine(root, ProductIdentity.DataDirectoryName, "Desktop");
        Directory.CreateDirectory(legacyRoot);

        Equal(legacyRoot, WorkspacePaths.ResolveDefaultRootDirectory(root),
            "仅存在旧数据时必须继续使用旧根，避免设置和任务看似丢失");

        Directory.CreateDirectory(canonicalRoot);
        Equal(canonicalRoot, WorkspacePaths.ResolveDefaultRootDirectory(root),
            "新数据根存在后必须优先使用 GAssetForge 根");
    }

    private static void ProjectPathsOpenLegacyManifestAndPreferCanonicalManifestWhenPresent()
    {
        var projectRoot = CreateRoot("legacy-project");
        var settingsRoot = Path.Combine(projectRoot, "ProjectSettings");
        Directory.CreateDirectory(settingsRoot);
        var legacyPath = Path.Combine(settingsRoot, ProductIdentity.LegacyProjectManifestFileName);
        var canonicalPath = Path.Combine(settingsRoot, ProductIdentity.ProjectManifestFileName);
        File.WriteAllText(legacyPath, "{}");

        Equal(legacyPath, new ProjectPaths(projectRoot).ManifestPath,
            "旧项目清单必须继续被发现");

        File.WriteAllText(canonicalPath, "{}");
        Equal(canonicalPath, new ProjectPaths(projectRoot).ManifestPath,
            "新旧清单同时存在时必须优先使用 canonical 清单");
    }

    private static void McpServerAcceptsLegacyDataRootEnvironmentVariableWithCanonicalPrecedence()
    {
        var originalCanonical = Environment.GetEnvironmentVariable(
            ProductIdentity.DataRootEnvironmentVariableName);
        var originalLegacy = Environment.GetEnvironmentVariable(
            ProductIdentity.LegacyDataRootEnvironmentVariableName);
        var legacyRoot = Path.Combine(CreateRoot("mcp-data-root"), "legacy");
        var canonicalRoot = Path.Combine(CreateRoot("mcp-data-root"), "canonical");
        try
        {
            Environment.SetEnvironmentVariable(ProductIdentity.DataRootEnvironmentVariableName, null);
            Environment.SetEnvironmentVariable(ProductIdentity.LegacyDataRootEnvironmentVariableName, legacyRoot);
            Equal(Path.GetFullPath(legacyRoot), ServerOptions.Parse([]).DataDirectory,
                "未设置新变量时 MCP 必须读取旧数据根变量");

            Environment.SetEnvironmentVariable(ProductIdentity.DataRootEnvironmentVariableName, canonicalRoot);
            Equal(Path.GetFullPath(canonicalRoot), ServerOptions.Parse([]).DataDirectory,
                "新旧变量同时存在时 MCP 必须优先读取新变量");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                ProductIdentity.DataRootEnvironmentVariableName,
                originalCanonical);
            Environment.SetEnvironmentVariable(
                ProductIdentity.LegacyDataRootEnvironmentVariableName,
                originalLegacy);
        }
    }

    private static string CreateRoot(string label)
    {
        var root = Path.Combine(Path.GetTempPath(), "GAssetForge.IdentitySmokeTests", label, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal(string expected, string actual, string message)
    {
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{message} Expected={expected}; Actual={actual}");
        }
    }
}
