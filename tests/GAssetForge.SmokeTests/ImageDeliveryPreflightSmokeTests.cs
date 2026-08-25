using GAssetForge.App.Services;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace GAssetForge.SmokeTests;

internal static class ImageDeliveryPreflightSmokeTests
{
    public static async Task RunAllAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.ImageDeliveryPreflightSmokeTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FreshPathEnvironmentMergesCurrentMachineAndUserEntries();
            await ConfiguredPythonPathTakesPrecedenceAsync(root);
            await FreshEnvironmentProviderDrivesPythonApiKeyAndCodexHomeAsync(root);
            await LocalAppDataPythonInstallIsDiscoveredWhenPathIsStaleAsync(root);
            await PyLauncherUsesPython3SelectionArgumentAsync(root);
            await PythonProbeDiagnosticsRedactApiKeyAsync(root);
            await CompleteLocalCliPrerequisitesRemainNetworkUnverifiedAsync(root);
            await ExistingButUnlaunchableImageGenScriptRemainsBuiltInOnlyAsync(root);
            await MissingOpenAiSdkRemainsBuiltInOnlyAsync(root);
            await LaterCompatiblePythonCandidateIsSelectedAsync(root);
            await MissingCliDependenciesRemainNonBlockingAsync(root);
            await ExistingValidModelOutputIsPreservedForResumeAsync(root);
            await ExistingInvalidModelOutputIsQuarantinedAsync(root);
            await TruncatedPngIsQuarantinedInsteadOfResumedAsync(root);
            await MissingOutputDirectoryBlocksDispatchAsync(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void FreshPathEnvironmentMergesCurrentMachineAndUserEntries()
    {
        var separator = Path.PathSeparator;
        var processEntry = Path.Combine(Path.GetTempPath(), "process-bin");
        var sharedEntry = Path.Combine(Path.GetTempPath(), "shared bin");
        var userEntry = Path.Combine(Path.GetTempPath(), "user-bin");
        var merged = ProcessEnvironmentSnapshot.MergePathValues(
        [
            $"{processEntry}{separator}\"{sharedEntry}\"",
            $"{userEntry}{separator}{sharedEntry}"
        ]);

        Equal(
            string.Join(separator, processEntry, sharedEntry, userEntry),
            merged,
            "最新 PATH 快照必须合并进程、Machine/User 条目并稳定去重");
    }

    private static async Task LocalAppDataPythonInstallIsDiscoveredWhenPathIsStaleAsync(string root)
    {
        var localAppData = Path.Combine(root, "local-app-data");
        var pythonPath = Path.Combine(
            localAppData,
            "Programs",
            "Python",
            "Python312",
            "python.exe");
        var codexHome = Path.Combine(root, "local-app-data-codex-home");
        var outputDirectory = Path.Combine(root, "local-app-data-output");
        Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllBytes(pythonPath, [0x4d, 0x5a]);
        var scriptPath = Path.Combine(
            codexHome,
            "skills",
            ".system",
            "imagegen",
            "scripts",
            "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, "# fixture");

        var preflight = new ImageDeliveryPreflight(
            pathProvider: () => string.Empty,
            localAppDataProvider: () => localAppData,
            codexHomeProvider: () => codexHome,
            apiKeyProvider: () => "fixture-api-key",
            executableProbe: new FixtureExecutableProbe(pythonPath));
        var result = await preflight.CheckAsync(
            [Path.Combine(outputDirectory, "model-output.png")],
            CancellationToken.None);

        Equal(ImageDeliveryPreflightState.LocalReadyNetworkUnverified, result.State,
            "用户级 Python 安装未进入旧 PATH 时仍应由 LocalAppData 常见目录发现");
        Equal(pythonPath, result.PythonExecutablePath,
            "LocalAppData 直探测必须返回经过真实启动验证的绝对 Python 路径");
    }

    private static async Task ConfiguredPythonPathTakesPrecedenceAsync(string root)
    {
        var configuredPython = Path.Combine(root, "configured-python", "python.exe");
        var localAppData = Path.Combine(root, "configured-local-app-data");
        var localPython = Path.Combine(
            localAppData,
            "Programs",
            "Python",
            "Python313",
            "python.exe");
        var pathBin = Path.Combine(root, "configured-path-bin");
        var pathPython = Path.Combine(pathBin, "python.exe");
        var codexHome = Path.Combine(root, "configured-codex-home");
        var outputDirectory = Path.Combine(root, "configured-output");
        Directory.CreateDirectory(Path.GetDirectoryName(configuredPython)!);
        Directory.CreateDirectory(Path.GetDirectoryName(localPython)!);
        Directory.CreateDirectory(pathBin);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllBytes(configuredPython, [0x4d, 0x5a]);
        File.WriteAllBytes(localPython, [0x4d, 0x5a]);
        File.WriteAllBytes(pathPython, [0x4d, 0x5a]);
        var scriptPath = Path.Combine(
            codexHome,
            "skills",
            ".system",
            "imagegen",
            "scripts",
            "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, "# fixture");

        var preflight = new ImageDeliveryPreflight(
            pathProvider: () => pathBin,
            configuredPythonPathProvider: () => configuredPython,
            localAppDataProvider: () => localAppData,
            codexHomeProvider: () => codexHome,
            apiKeyProvider: () => "fixture-api-key",
            executableProbe: new FixtureExecutableProbe(
                configuredPython,
                localPython,
                pathPython));
        var result = await preflight.CheckAsync(
            [Path.Combine(outputDirectory, "model-output.png")],
            CancellationToken.None);

        Equal(configuredPython, result.PythonExecutablePath,
            "手动配置的 Python 必须优先于 LocalAppData 与 PATH 自动探测候选");
    }

    private static async Task FreshEnvironmentProviderDrivesPythonApiKeyAndCodexHomeAsync(string root)
    {
        var configuredPython = Path.Combine(root, "fresh-env-python", "python.exe");
        var codexHome = Path.Combine(root, "fresh-env-codex-home");
        var outputDirectory = Path.Combine(root, "fresh-env-output");
        var scriptPath = Path.Combine(
            codexHome,
            "skills",
            ".system",
            "imagegen",
            "scripts",
            "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(configuredPython)!);
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllBytes(configuredPython, [0x4d, 0x5a]);
        File.WriteAllText(scriptPath, "# fixture");
        string? ReadFreshEnvironment(string name) => name switch
        {
            "GASSETFORGE_PYTHON_EXECUTABLE" => configuredPython,
            "CODEX_HOME" => codexHome,
            "OPENAI_API_KEY" => "fresh-environment-api-key",
            _ => null
        };
        var preflight = new ImageDeliveryPreflight(
            pathProvider: () => string.Empty,
            localAppDataProvider: () => null,
            executableProbe: new FixtureExecutableProbe(configuredPython),
            environmentVariableProvider: ReadFreshEnvironment);

        var result = await preflight.CheckAsync(
            [Path.Combine(outputDirectory, "model-output.png")],
            CancellationToken.None);

        Equal(ImageDeliveryPreflightState.LocalReadyNetworkUnverified, result.State,
            "UI 图片交付预检必须使用与 MCP Harness 一致的 fresh 环境读取语义");
        Equal(configuredPython, result.PythonExecutablePath,
            "fresh GASSETFORGE_PYTHON_EXECUTABLE 必须驱动 UI Python 探测");
        Equal(scriptPath, result.ImageGenScriptPath,
            "fresh CODEX_HOME 必须驱动 UI image_gen.py 定位");
        True(result.ApiKeyAvailable,
            "fresh OPENAI_API_KEY 必须反映为 UI CLI 本地前置可用");
    }

    private static async Task PyLauncherUsesPython3SelectionArgumentAsync(string root)
    {
        var bin = Path.Combine(root, "py-launcher-bin");
        var codexHome = Path.Combine(root, "py-launcher-codex-home");
        var outputDirectory = Path.Combine(root, "py-launcher-output");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(outputDirectory);
        var pyLauncherPath = Path.Combine(bin, "py.exe");
        File.WriteAllBytes(pyLauncherPath, [0x4d, 0x5a]);
        var scriptPath = Path.Combine(
            codexHome,
            "skills",
            ".system",
            "imagegen",
            "scripts",
            "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, "# fixture");

        var probe = new Py3LauncherExecutableProbe(pyLauncherPath);
        var preflight = new ImageDeliveryPreflight(
            pathProvider: () => bin,
            localAppDataProvider: () => null,
            codexHomeProvider: () => codexHome,
            apiKeyProvider: () => "fixture-api-key",
            executableProbe: probe);
        var result = await preflight.CheckAsync(
            [Path.Combine(outputDirectory, "model-output.png")],
            CancellationToken.None);

        Equal(ImageDeliveryPreflightState.LocalReadyNetworkUnverified, result.State,
            "Windows py 启动器应以 py -3 合同探测并运行 image_gen.py");
        True(probe.AllProbesUsedPython3Selection,
            "py 启动器的版本、脚本和 SDK 探针都必须保留 -3 前缀参数");
        Equal("-3", string.Join(' ', result.PythonLauncherArguments),
            "结构化预检结果必须保留 py -3，供软件 Harness 使用");
        var prompt = GenerationAgentPromptBuilder.Build(["fixture-task"], result);
        True(prompt.Contains("run_image_delivery_fallback", StringComparison.Ordinal) &&
             !prompt.Contains(pyLauncherPath, StringComparison.Ordinal),
            "Agent 应调用 Harness 兜底，不应自行执行或丢失 py -3 启动合同");
    }

    private static async Task PythonProbeDiagnosticsRedactApiKeyAsync(string root)
    {
        const string apiKey = "sk-fixture-image-delivery-secret";
        var bin = Path.Combine(root, "secret-probe-bin");
        var codexHome = Path.Combine(root, "secret-probe-codex-home");
        var outputDirectory = Path.Combine(root, "secret-probe-output");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(outputDirectory);
        var pythonPath = Path.Combine(bin, "python.exe");
        File.WriteAllBytes(pythonPath, [0x4d, 0x5a]);
        var scriptPath = Path.Combine(
            codexHome,
            "skills",
            ".system",
            "imagegen",
            "scripts",
            "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, "# fixture");

        var preflight = new ImageDeliveryPreflight(
            pathProvider: () => bin,
            localAppDataProvider: () => null,
            codexHomeProvider: () => codexHome,
            apiKeyProvider: () => apiKey,
            executableProbe: new SecretEchoExecutableProbe(apiKey));
        var result = await preflight.CheckAsync(
            [Path.Combine(outputDirectory, "model-output.png")],
            CancellationToken.None);

        False(result.StatusText.Contains(apiKey, StringComparison.Ordinal),
            "Python 启动失败诊断不得把 OPENAI_API_KEY 回显到 UI 或工作台日志");
        False(result.Diagnostics.Any(message => message.Contains(apiKey, StringComparison.Ordinal)),
            "结构化预检 Diagnostics 也不得保留探针意外回显的 API Key");
        True(result.StatusText.Contains("[REDACTED]", StringComparison.Ordinal),
            "被探针输出意外回显的 API Key 应替换为明确脱敏标记");
    }

    private static async Task ExistingValidModelOutputIsPreservedForResumeAsync(string root)
    {
        var outputDirectory = Path.Combine(root, "existing-valid-output");
        Directory.CreateDirectory(outputDirectory);
        var sourcePath = Path.Combine(outputDirectory, "model-output.png");
        Program.WritePngFixture(sourcePath, 8, 8, includeAlpha: true);
        var originalBytes = File.ReadAllBytes(sourcePath);
        var preflight = new ImageDeliveryPreflight(
            pathProvider: () => string.Empty,
            codexHomeProvider: () => Path.Combine(root, "missing-codex-home-for-resume"),
            apiKeyProvider: () => null,
            executableProbe: new FixtureExecutableProbe());

        var result = await preflight.CheckAsync([sourcePath], CancellationToken.None);

        True(result.CanDispatch, "已落盘的有效模型 PNG 必须允许恢复调度");
        True(File.Exists(sourcePath), "有效的精确 modelOutput 不得在预检时被覆盖或移动");
        True(originalBytes.SequenceEqual(File.ReadAllBytes(sourcePath)),
            "有效 modelOutput 必须逐字节保留给 submit_generation_output");
        True(result.StatusText.Contains("可直接提交", StringComparison.Ordinal),
            "预检必须明确提示 Agent 恢复提交现有精确 PNG");
    }

    private static async Task ExistingInvalidModelOutputIsQuarantinedAsync(string root)
    {
        var outputDirectory = Path.Combine(root, "existing-invalid-output");
        Directory.CreateDirectory(outputDirectory);
        var sourcePath = Path.Combine(outputDirectory, "model-output.png");
        File.WriteAllText(sourcePath, "not-a-real-png");
        var preflight = new ImageDeliveryPreflight(
            pathProvider: () => string.Empty,
            codexHomeProvider: () => Path.Combine(root, "missing-codex-home-for-quarantine"),
            apiKeyProvider: () => null,
            executableProbe: new FixtureExecutableProbe());

        var result = await preflight.CheckAsync([sourcePath], CancellationToken.None);

        True(result.CanDispatch, "损坏模型文件隔离成功后应允许重新生成");
        False(File.Exists(sourcePath), "损坏 modelOutput 必须让出精确推荐路径");
        var quarantined = Directory.EnumerateFiles(
                outputDirectory,
                "model-output.png.invalid-*.quarantine")
            .SingleOrDefault();
        True(quarantined is not null && File.ReadAllText(quarantined) == "not-a-real-png",
            "损坏模型文件必须可恢复地保留在同目录隔离文件中");
        True(result.StatusText.Contains("已安全隔离", StringComparison.Ordinal),
            "预检必须向用户明确报告损坏文件的隔离位置");
    }

    private static async Task TruncatedPngIsQuarantinedInsteadOfResumedAsync(string root)
    {
        var outputDirectory = Path.Combine(root, "truncated-png-output");
        Directory.CreateDirectory(outputDirectory);
        var sourcePath = Path.Combine(outputDirectory, "model-output.png");
        Program.WritePngFixture(sourcePath, 64, 64, includeAlpha: true);
        var bytes = File.ReadAllBytes(sourcePath);
        var truncated = TruncateInsideFirstIdat(bytes);
        File.WriteAllBytes(sourcePath, truncated);

        var preflight = new ImageDeliveryPreflight(
            pathProvider: () => string.Empty,
            codexHomeProvider: () => Path.Combine(root, "missing-codex-home-for-truncated-png"),
            apiKeyProvider: () => null,
            executableProbe: new FixtureExecutableProbe());
        var result = await preflight.CheckAsync([sourcePath], CancellationToken.None);

        True(result.CanDispatch, "截断 PNG 隔离成功后应允许重新生成");
        False(File.Exists(sourcePath),
            "只有 PNG 头部可识别但 IDAT 截断的文件不得被保留为可恢复产物");
        True(Directory.EnumerateFiles(
                outputDirectory,
                "model-output.png.invalid-*.quarantine")
            .Any(),
            "截断 PNG 必须被安全隔离，避免每次重试重复提交同一损坏文件");
        True(result.StatusText.Contains("已安全隔离", StringComparison.Ordinal),
            "完整解码失败应作为明确隔离诊断显示");
    }

    private static byte[] TruncateInsideFirstIdat(byte[] pngBytes)
    {
        var offset = 8;
        while (offset + 12 <= pngBytes.Length)
        {
            var dataLength = BinaryPrimitives.ReadInt32BigEndian(pngBytes.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(pngBytes, offset + 4, 4);
            if (dataLength < 0 || offset + 12L + dataLength > pngBytes.Length)
            {
                break;
            }

            if (string.Equals(type, "IDAT", StringComparison.Ordinal) && dataLength > 1)
            {
                var truncatedLength = offset + 8 + Math.Max(1, dataLength / 2);
                return pngBytes[..truncatedLength];
            }

            offset += 12 + dataLength;
        }

        throw new InvalidOperationException("PNG fixture 缺少可截断的 IDAT chunk。");
    }

    private static async Task CompleteLocalCliPrerequisitesRemainNetworkUnverifiedAsync(string root)
    {
        var bin = Path.Combine(root, "ready-bin");
        var codexHome = Path.Combine(root, "ready-codex-home");
        var outputDirectory = Path.Combine(root, "ready-output");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(outputDirectory);
        var pythonPath = Path.Combine(bin, "python.exe");
        File.WriteAllBytes(pythonPath, [0x4d, 0x5a]);
        var scriptPath = Path.Combine(
            codexHome,
            "skills",
            ".system",
            "imagegen",
            "scripts",
            "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, "# fixture");

        var probe = new FixtureExecutableProbe(pythonPath);
        var preflight = new ImageDeliveryPreflight(
            pathProvider: () => bin,
            codexHomeProvider: () => codexHome,
            apiKeyProvider: () => "fixture-api-key",
            executableProbe: probe);
        var result = await preflight.CheckAsync(
            [Path.Combine(outputDirectory, "model-output.png")],
            CancellationToken.None);

        Equal(ImageDeliveryPreflightState.LocalReadyNetworkUnverified, result.State,
            "本地 CLI 依赖齐备时仍不得把 Image API 网络误报为 Ready");
        True(result.CanDispatch, "本地 CLI 前置齐备时必须允许启动 Agent Turn");
        True(result.CliFallbackLocalPrerequisitesReady,
            "Python、skill、API Key 与输出目录齐备时应报告本地前置就绪");
        False(result.CliFallbackReady,
            "未在受管 Agent sandbox 内验证 Image API 前不得报告 CLI 兜底 Ready");
        Equal(ImageDeliveryNetworkState.NotVerified, result.NetworkState,
            "本地预检不能代替 Agent sandbox 网络验证");
        Equal(pythonPath, result.PythonExecutablePath, "应保留经过启动探测的 Python 路径");
        Equal(scriptPath, result.ImageGenScriptPath, "应返回官方 image_gen.py 路径");
        True(result.StatusText.Contains("CLI 本地前置已就绪", StringComparison.Ordinal) &&
             result.StatusText.Contains("网络未验证", StringComparison.Ordinal),
            "UI 必须分开显示 local prerequisites 与 network-not-verified");
        True(!Directory.EnumerateFiles(outputDirectory).Any(),
            "输出目录可写探针不得留下临时文件");
        Equal(1, probe.ScriptProbeCount,
            "本地 CLI 前置就绪前必须真实执行一次 image_gen.py --help");
        Equal(1, probe.OpenAiSdkProbeCount,
            "本地 CLI 前置就绪前必须验证 image_gen.py 所需的 OpenAI SDK");
        var prompt = GenerationAgentPromptBuilder.Build(["fixture-task"], result);
        True(prompt.Contains("run_image_delivery_fallback", StringComparison.Ordinal) &&
             !prompt.Contains(pythonPath, StringComparison.Ordinal) &&
             !prompt.Contains(scriptPath, StringComparison.Ordinal),
            "解释器和脚本路径只应由软件 Harness 消费，Agent 不得依赖旧 PATH 自行执行 CLI");
    }

    private static async Task ExistingButUnlaunchableImageGenScriptRemainsBuiltInOnlyAsync(string root)
    {
        var bin = Path.Combine(root, "broken-script-bin");
        var codexHome = Path.Combine(root, "broken-script-codex-home");
        var outputDirectory = Path.Combine(root, "broken-script-output");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(outputDirectory);
        var pythonPath = Path.Combine(bin, "python.exe");
        File.WriteAllBytes(pythonPath, [0x4d, 0x5a]);
        var scriptPath = Path.Combine(
            codexHome,
            "skills",
            ".system",
            "imagegen",
            "scripts",
            "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, "raise SyntaxError('fixture')");

        var probe = new FixtureExecutableProbe(scriptProbeSucceeds: false, pythonPath);
        var preflight = new ImageDeliveryPreflight(
            pathProvider: () => bin,
            codexHomeProvider: () => codexHome,
            apiKeyProvider: () => "fixture-api-key",
            executableProbe: probe);
        var result = await preflight.CheckAsync(
            [Path.Combine(outputDirectory, "model-output.png")],
            CancellationToken.None);

        Equal(ImageDeliveryPreflightState.BuiltInOnly, result.State,
            "image_gen.py 存在但无法由已探测 Python 启动时必须降级为 BuiltInOnly");
        False(result.CliFallbackLocalPrerequisitesReady,
            "脚本语法或依赖不兼容时不得误报 CLI 本地前置就绪");
        Equal(1, probe.ScriptProbeCount,
            "必须通过真实脚本启动探测发现 image_gen.py 不兼容");
        Equal(0, probe.OpenAiSdkProbeCount,
            "image_gen.py --help 已失败时不应继续误探测 SDK");
        True(result.StatusText.Contains("image_gen.py", StringComparison.Ordinal) &&
             result.StatusText.Contains("fixture script launch rejected", StringComparison.Ordinal),
            "脚本启动失败必须在 UI 诊断中保留明确原因");
    }

    private static async Task MissingOpenAiSdkRemainsBuiltInOnlyAsync(string root)
    {
        var bin = Path.Combine(root, "missing-sdk-bin");
        var codexHome = Path.Combine(root, "missing-sdk-codex-home");
        var outputDirectory = Path.Combine(root, "missing-sdk-output");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(outputDirectory);
        var pythonPath = Path.Combine(bin, "python.exe");
        File.WriteAllBytes(pythonPath, [0x4d, 0x5a]);
        var scriptPath = Path.Combine(
            codexHome,
            "skills",
            ".system",
            "imagegen",
            "scripts",
            "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, "# fixture compatible with --help");

        var probe = new FixtureExecutableProbe(
            scriptProbeSucceeds: true,
            openAiSdkProbeSucceeds: false,
            pythonPath);
        var preflight = new ImageDeliveryPreflight(
            pathProvider: () => bin,
            codexHomeProvider: () => codexHome,
            apiKeyProvider: () => "fixture-api-key",
            executableProbe: probe);
        var result = await preflight.CheckAsync(
            [Path.Combine(outputDirectory, "model-output.png")],
            CancellationToken.None);

        Equal(ImageDeliveryPreflightState.BuiltInOnly, result.State,
            "Python 和 image_gen.py 可启动但缺少 OpenAI SDK 时必须降级为 BuiltInOnly");
        False(result.CliFallbackLocalPrerequisitesReady,
            "缺少 OpenAI SDK 时不得误报 CLI 本地前置就绪");
        Equal(1, probe.ScriptProbeCount, "应先验证 image_gen.py --help");
        Equal(1, probe.OpenAiSdkProbeCount, "必须真实验证 OpenAI SDK import");
        True(result.StatusText.Contains("OpenAI SDK", StringComparison.Ordinal) &&
             result.StatusText.Contains("fixture OpenAI SDK import rejected", StringComparison.Ordinal),
            "SDK 缺失必须在 UI 中保留准确诊断");
    }

    private static async Task LaterCompatiblePythonCandidateIsSelectedAsync(string root)
    {
        var bin = Path.Combine(root, "multi-python-bin");
        var codexHome = Path.Combine(root, "multi-python-codex-home");
        var outputDirectory = Path.Combine(root, "multi-python-output");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(outputDirectory);
        var incompatiblePython = Path.Combine(bin, "python.exe");
        var compatiblePython = Path.Combine(bin, "python3.exe");
        File.WriteAllBytes(incompatiblePython, [0x4d, 0x5a]);
        File.WriteAllBytes(compatiblePython, [0x4d, 0x5a]);
        var scriptPath = Path.Combine(
            codexHome,
            "skills",
            ".system",
            "imagegen",
            "scripts",
            "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, "# fixture");

        var probe = new MultiPythonExecutableProbe(incompatiblePython, compatiblePython);
        var preflight = new ImageDeliveryPreflight(
            pathProvider: () => bin,
            codexHomeProvider: () => codexHome,
            apiKeyProvider: () => "fixture-api-key",
            executableProbe: probe);
        var result = await preflight.CheckAsync(
            [Path.Combine(outputDirectory, "model-output.png")],
            CancellationToken.None);

        Equal(ImageDeliveryPreflightState.LocalReadyNetworkUnverified, result.State,
            "首个 Python 缺少 SDK 时必须继续尝试后续候选");
        Equal(compatiblePython, result.PythonExecutablePath,
            "预检必须返回真正通过脚本与 SDK 探针的 Python 绝对路径");
        Equal(2, probe.ScriptProbeCount, "两个可启动 Python 都应执行脚本兼容探针");
        Equal(2, probe.OpenAiSdkProbeCount, "首个 SDK 失败后必须继续探测后续 Python");
    }

    private static async Task MissingCliDependenciesRemainNonBlockingAsync(string root)
    {
        var outputDirectory = Path.Combine(root, "built-in-only-output");
        Directory.CreateDirectory(outputDirectory);
        var preflight = new ImageDeliveryPreflight(
            pathProvider: () => string.Empty,
            codexHomeProvider: () => Path.Combine(root, "missing-codex-home"),
            apiKeyProvider: () => null,
            executableProbe: new FixtureExecutableProbe());
        var result = await preflight.CheckAsync(
            [Path.Combine(outputDirectory, "model-output.png")],
            CancellationToken.None);

        Equal(ImageDeliveryPreflightState.BuiltInOnly, result.State,
            "CLI 依赖缺失但项目输出可写时应降级为 BuiltInOnly");
        True(result.CanDispatch,
            "CLI 兜底缺失不得阻断能返回明确本地路径的内置 ImageGen");
        False(result.CliFallbackReady, "缺少依赖时不得误报 CLI 兜底可用");
        False(result.CliFallbackLocalPrerequisitesReady,
            "缺少 Python、skill 或 API Key 时本地 CLI 前置应为不可用");
        True(result.StatusText.Contains("Python/py", StringComparison.Ordinal) &&
             result.StatusText.Contains("image_gen.py", StringComparison.Ordinal) &&
             result.StatusText.Contains("OPENAI_API_KEY", StringComparison.Ordinal) &&
             result.StatusText.Contains("内置 ImageGen", StringComparison.Ordinal),
            "降级状态必须一次说明所有缺失能力和仍可继续的内置路径");
    }

    private static async Task MissingOutputDirectoryBlocksDispatchAsync(string root)
    {
        var preflight = new ImageDeliveryPreflight(
            pathProvider: () => string.Empty,
            codexHomeProvider: () => Path.Combine(root, "missing-output-codex-home"),
            apiKeyProvider: () => null,
            executableProbe: new FixtureExecutableProbe());
        var result = await preflight.CheckAsync(
            [Path.Combine(root, "directory-does-not-exist", "model-output.png")],
            CancellationToken.None);

        Equal(ImageDeliveryPreflightState.OutputUnavailable, result.State,
            "推荐路径父目录不存在时应判定项目输出不可用");
        False(result.CanDispatch, "项目内推荐输出不可写时不得启动必然失败的 Agent Turn");
        True(result.StatusText.Contains("输出目录不可写", StringComparison.Ordinal),
            "输出阻断应提供可操作的诊断文本");
    }

    private sealed class FixtureExecutableProbe : IImageDeliveryExecutableProbe
    {
        private readonly HashSet<string> _successfulPaths;
        private readonly bool _scriptProbeSucceeds;
        private readonly bool _openAiSdkProbeSucceeds;

        public FixtureExecutableProbe(params string[] successfulPaths)
            : this(
                scriptProbeSucceeds: true,
                openAiSdkProbeSucceeds: true,
                successfulPaths)
        {
        }

        public FixtureExecutableProbe(bool scriptProbeSucceeds, params string[] successfulPaths)
            : this(scriptProbeSucceeds, openAiSdkProbeSucceeds: true, successfulPaths)
        {
        }

        public FixtureExecutableProbe(
            bool scriptProbeSucceeds,
            bool openAiSdkProbeSucceeds,
            params string[] successfulPaths)
        {
            _scriptProbeSucceeds = scriptProbeSucceeds;
            _openAiSdkProbeSucceeds = openAiSdkProbeSucceeds;
            _successfulPaths = new HashSet<string>(
                successfulPaths.Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
        }

        public int ScriptProbeCount { get; private set; }

        public int OpenAiSdkProbeCount { get; private set; }

        public Task<ImageDeliveryExecutableProbeResult> ProbeAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = Path.GetFullPath(executablePath);
            var isScriptProbe = arguments.Any(argument =>
                argument.EndsWith("image_gen.py", StringComparison.OrdinalIgnoreCase));
            var isOpenAiSdkProbe = arguments.Count >= 2 &&
                                   string.Equals(arguments[0], "-c", StringComparison.Ordinal) &&
                                   arguments[1].Contains("from openai import OpenAI", StringComparison.Ordinal);
            if (isScriptProbe)
            {
                ScriptProbeCount++;
            }

            if (isOpenAiSdkProbe)
            {
                OpenAiSdkProbeCount++;
            }

            if (!_successfulPaths.Contains(normalized))
            {
                return Task.FromResult(
                    new ImageDeliveryExecutableProbeResult(false, "fixture launch rejected"));
            }

            return Task.FromResult(isScriptProbe && !_scriptProbeSucceeds
                ? new ImageDeliveryExecutableProbeResult(false, "fixture script launch rejected")
                : isOpenAiSdkProbe && !_openAiSdkProbeSucceeds
                    ? new ImageDeliveryExecutableProbeResult(false, "fixture OpenAI SDK import rejected")
                : new ImageDeliveryExecutableProbeResult(true, "Python 3.12 fixture"));
        }
    }

    private sealed class MultiPythonExecutableProbe(
        string incompatiblePython,
        string compatiblePython) : IImageDeliveryExecutableProbe
    {
        private readonly string _incompatiblePython = Path.GetFullPath(incompatiblePython);
        private readonly string _compatiblePython = Path.GetFullPath(compatiblePython);

        public int ScriptProbeCount { get; private set; }

        public int OpenAiSdkProbeCount { get; private set; }

        public Task<ImageDeliveryExecutableProbeResult> ProbeAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = Path.GetFullPath(executablePath);
            if (!string.Equals(normalized, _incompatiblePython, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalized, _compatiblePython, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(
                    new ImageDeliveryExecutableProbeResult(false, "unexpected Python fixture"));
            }

            var isScriptProbe = arguments.Any(argument =>
                argument.EndsWith("image_gen.py", StringComparison.OrdinalIgnoreCase));
            var isSdkProbe = arguments.Count >= 2 &&
                             string.Equals(arguments[0], "-c", StringComparison.Ordinal) &&
                             arguments[1].Contains("from openai import OpenAI", StringComparison.Ordinal);
            if (isScriptProbe)
            {
                ScriptProbeCount++;
            }

            if (isSdkProbe)
            {
                OpenAiSdkProbeCount++;
                if (string.Equals(normalized, _incompatiblePython, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new ImageDeliveryExecutableProbeResult(
                        false,
                        "fixture first Python lacks OpenAI SDK"));
                }
            }

            return Task.FromResult(
                new ImageDeliveryExecutableProbeResult(true, "fixture Python ready"));
        }
    }

    private sealed class Py3LauncherExecutableProbe(string pyLauncherPath) : IImageDeliveryExecutableProbe
    {
        private readonly string _pyLauncherPath = Path.GetFullPath(pyLauncherPath);

        public bool AllProbesUsedPython3Selection { get; private set; } = true;

        public Task<ImageDeliveryExecutableProbeResult> ProbeAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = Path.GetFullPath(executablePath);
            var usesPython3Selection = arguments.Count > 0 &&
                                       string.Equals(arguments[0], "-3", StringComparison.Ordinal);
            AllProbesUsedPython3Selection &= usesPython3Selection;
            return Task.FromResult(
                string.Equals(normalized, _pyLauncherPath, StringComparison.OrdinalIgnoreCase) &&
                usesPython3Selection
                    ? new ImageDeliveryExecutableProbeResult(true, "Python 3.12 launcher fixture")
                    : new ImageDeliveryExecutableProbeResult(false, "fixture requires py -3"));
        }
    }

    private sealed class SecretEchoExecutableProbe(string secret) : IImageDeliveryExecutableProbe
    {
        public Task<ImageDeliveryExecutableProbeResult> ProbeAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ImageDeliveryExecutableProbeResult(
                false,
                $"fixture process echoed OPENAI_API_KEY={secret}"));
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }
}
