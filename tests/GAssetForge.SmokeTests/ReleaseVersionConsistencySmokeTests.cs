using GAssetForge.Core;
using GAssetForge.Core.Generation;
using GAssetForge.McpServer;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GAssetForge.SmokeTests;

internal static class ReleaseVersionConsistencySmokeTests
{
    private const string ExpectedReleaseVersion = "1.0.11t1";

    public static async Task RunAsync(string temporaryRoot)
    {
        var assemblyInformationalVersion = ProductIdentity.InformationalVersion;

        var dataRoot = Path.Combine(temporaryRoot, "release-version-consistency");
        Directory.CreateDirectory(dataRoot);
        var options = ServerOptions.Parse(new[] { "--data-root", dataRoot });
        var taskStore = new JsonGenerationTaskStore(options.GenerationStorageRoot);
        var provider = new GenerationToolProvider(
            taskStore,
            new WorkspaceContextResolver(options),
            options,
            taskStore.StoreFilePath);
        var catalog = provider.CreateCatalog();

        var serverInfoResult = await catalog.CallAsync(
            "server_info",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);
        var serverInfoVersion = JsonSerializer.SerializeToElement(serverInfoResult, ProtocolJson.Options)
            .GetProperty("structuredContent")
            .GetProperty("version")
            .GetString();

        var initializeResult = await new McpRequestDispatcher(catalog).DispatchAsync(
            "initialize",
            JsonSerializer.SerializeToElement(new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "Release Version SmokeTests", version = "0" }
            }),
            new McpSessionState(),
            expectedAgent: null,
            CancellationToken.None);
        var initializeVersion = JsonSerializer.SerializeToElement(initializeResult, ProtocolJson.Options)
            .GetProperty("serverInfo")
            .GetProperty("version")
            .GetString();

        var desktopExecutablePath = Path.Combine(AppContext.BaseDirectory, "GAssetForge.exe");
        if (!File.Exists(desktopExecutablePath))
        {
            throw new InvalidOperationException($"版本一致性测试缺少桌面 EXE：{desktopExecutablePath}");
        }

        var executableProductVersion = FileVersionInfo
            .GetVersionInfo(desktopExecutablePath)
            .ProductVersion;

        Equal(ExpectedReleaseVersion, assemblyInformationalVersion,
            "程序集正式 InformationalVersion");
        Equal(assemblyInformationalVersion, serverInfoVersion,
            "server_info.version 必须读取程序集 InformationalVersion");
        Equal(assemblyInformationalVersion, initializeVersion,
            "MCP initialize.serverInfo.version 必须读取程序集 InformationalVersion");
        Equal(assemblyInformationalVersion, executableProductVersion,
            "GAssetForge.exe ProductVersion 必须与程序集 InformationalVersion 完全一致");
    }

    private static void Equal(string expected, string? actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{message}。期望：{expected}，实际：{actual ?? "<null>"}");
        }
    }
}
