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
        True(prompt.Contains(pythonPath, StringComparison.Ordinal) &&
             prompt.Contains(scriptPath, StringComparison.Ordinal) &&
             prompt.Contains("必须使用预检给出的绝对 Python 与 image_gen.py 路径", StringComparison.Ordinal),
            "Agent CLI 兜底必须使用预检成功的绝对解释器和脚本路径，不能依赖旧 PATH");
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
