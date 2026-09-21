using GAssetForge.Core;
using GAssetForge.Core.Generation;
using GAssetForge.McpServer;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GAssetForge.SmokeTests;

internal static class ReleaseVersionConsistencySmokeTests
{
    private const string ExpectedReleaseVersion = "1.0.26t1";
    private const string ExpectedFileVersion = "1.0.26.0";

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
        var initializePayload = JsonSerializer.SerializeToElement(initializeResult, ProtocolJson.Options);
        var initializeVersion = initializePayload
            .GetProperty("serverInfo")
            .GetProperty("version")
            .GetString();
        var initializeInstructions = initializePayload
            .GetProperty("instructions")
            .GetString() ?? string.Empty;

        var desktopExecutablePath = Path.Combine(AppContext.BaseDirectory, "GAssetForge.exe");
        if (!File.Exists(desktopExecutablePath))
        {
            throw new InvalidOperationException($"版本一致性测试缺少桌面 EXE：{desktopExecutablePath}");
        }

        var desktopVersionInfo = FileVersionInfo.GetVersionInfo(desktopExecutablePath);
        var mcpExecutablePath = Path.Combine(AppContext.BaseDirectory, "GAssetForge.McpServer.exe");
        if (!File.Exists(mcpExecutablePath))
        {
            throw new InvalidOperationException($"版本一致性测试缺少 MCP EXE：{mcpExecutablePath}");
        }

        var mcpVersionInfo = FileVersionInfo.GetVersionInfo(mcpExecutablePath);

        Equal(ExpectedReleaseVersion, assemblyInformationalVersion,
            "程序集正式 InformationalVersion");
        Equal(assemblyInformationalVersion, serverInfoVersion,
            "server_info.version 必须读取程序集 InformationalVersion");
        Equal(assemblyInformationalVersion, initializeVersion,
            "MCP initialize.serverInfo.version 必须读取程序集 InformationalVersion");
        Contains(initializeInstructions, "submit_generation_output",
            "MCP initialize.instructions 必须指向当前单图提交合同");
        Contains(initializeInstructions, "run_image_delivery_fallback",
            "MCP initialize.instructions 必须说明内置图片没有本地路径时的 Harness 交付路径");
        NotContains(initializeInstructions, "必须把产物保存到返回的有效 Assets 根目录内",
            "MCP initialize.instructions 不得继续要求 Agent 自行写入 Assets");
        NotContains(initializeInstructions, "先调用 get_current_context",
            "MCP initialize.instructions 不得强制调用可能被客户端工具白名单隐藏的上下文工具");
        Equal(assemblyInformationalVersion, desktopVersionInfo.ProductVersion,
            "GAssetForge.exe ProductVersion 必须与程序集 InformationalVersion 完全一致");
        Equal(assemblyInformationalVersion, mcpVersionInfo.ProductVersion,
            "GAssetForge.McpServer.exe ProductVersion 必须与程序集 InformationalVersion 完全一致");
        Equal(ExpectedFileVersion, desktopVersionInfo.FileVersion,
            "GAssetForge.exe FileVersion 必须与正式文件版本完全一致");
        Equal(ExpectedFileVersion, mcpVersionInfo.FileVersion,
            "GAssetForge.McpServer.exe FileVersion 必须与正式文件版本完全一致");
    }

    private static void Equal(string expected, string? actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{message}。期望：{expected}，实际：{actual ?? "<null>"}");
        }
    }

    private static void Contains(string value, string expected, string message)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{message}。缺少：{expected}");
        }
    }

    private static void NotContains(string value, string unexpected, string message)
    {
        if (value.Contains(unexpected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{message}。不应包含：{unexpected}");
        }
    }
}
