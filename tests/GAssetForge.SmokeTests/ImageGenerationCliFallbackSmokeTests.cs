using GAssetForge.McpServer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace GAssetForge.SmokeTests;

internal static class ImageGenerationCliFallbackSmokeTests
{
    internal static async Task RunAllAsync(string temporaryRoot)
    {
        await GenerateUsesFrozenPromptAndExactOutputAsync(temporaryRoot);
        await EditRepeatsReferencesAndPreservesPyLauncherPrefixAsync(temporaryRoot);
        await ExistingValidPngSkipsProbeAndImageApiAsync(temporaryRoot);
        await InvalidExistingPngIsRegeneratedAsync(temporaryRoot);
        await ConcurrentDuplicateCallsShareSingleGenerationAsync(temporaryRoot);
        await LocalAppDataPythonUsesSemanticVersionOrderAsync(temporaryRoot);
        await ProbeFallsBackToPyLauncherWithPython3PrefixAsync(temporaryRoot);
        await CliFailureRedactsApiKeyAsync(temporaryRoot);
        await NetworkEnvironmentIsForwardedAndRedactedAsync(temporaryRoot);
        await SuccessfulExitWithoutValidPngIsRejectedAsync(temporaryRoot);
        await OutputCaptureStopsWaitingWhenPipeHandlesRemainOpenAsync();
    }

    private static async Task OutputCaptureStopsWaitingWhenPipeHandlesRemainOpenAsync()
    {
        var stdout = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderr = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readersClosed = false;
        var stopwatch = Stopwatch.StartNew();

        var capture = await SystemImageGenerationCliProcessRunner.CaptureOutputAsync(
            stdout.Task,
            stderr.Task,
            () => readersClosed = true,
            TimeSpan.FromMilliseconds(50));

        stopwatch.Stop();
        True(readersClosed, "输出管道超时后必须主动关闭 reader");
        True(capture.DrainTimedOut, "永不关闭的输出管道必须返回明确的 drain timeout");
        True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            "后代进程持有 stdout/stderr 时不得无界等待");
        True(capture.StandardError.Contains("输出管道", StringComparison.Ordinal) &&
             capture.StandardError.Contains("已停止等待", StringComparison.Ordinal),
            "管道排空超时必须保留可诊断错误");

        var fastReadersClosed = false;
        var completed = await SystemImageGenerationCliProcessRunner.CaptureOutputAsync(
            Task.FromResult("stdout-ready"),
            Task.FromResult("stderr-ready"),
            () => fastReadersClosed = true,
            TimeSpan.FromMilliseconds(50));
        False(fastReadersClosed, "已正常关闭的输出管道不得被强制终止");
        False(completed.DrainTimedOut, "正常输出不得标记 drain timeout");
        Equal("stdout-ready", completed.StandardOutput, "正常 stdout 必须原样保留");
        Equal("stderr-ready", completed.StandardError, "正常 stderr 必须原样保留");
    }

    private static async Task GenerateUsesFrozenPromptAndExactOutputAsync(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "image-generation-cli-fallback", Guid.NewGuid().ToString("N"));
        var promptPath = Path.Combine(root, "20-prompts", "task.prompt.txt");
        var outputPath = Path.Combine(root, "30-model-output", "task.model.png");
        var scriptPath = Path.Combine(root, "codex", "skills", ".system", "imagegen", "scripts", "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(scriptPath, "# smoke-test image_gen.py");

        var frozenPrompt = "一块无旋转的正方形 Tilemap 瓦片。\r\n必须保持水平与垂直屏幕轴。\r\n";
        var probe = new FixedProbe(new ImageGenerationCliFallbackProbeResult(
            new ImageGenerationPythonCommand("C:\\Python312\\python.exe", [], "C:\\Python312\\python.exe"),
            scriptPath,
            "test-api-key",
            null,
            "C:\\Python312",
            "测试 Python 与 image_gen.py 已就绪。"));
        var runner = new RecordingRunner(request =>
        {
            WritePng(outputPath);
            return new ImageGenerationCliProcessResult(0, $"Wrote {outputPath}", string.Empty, false);
        });
        var service = new ImageGenerationCliFallbackService(probe, runner);
        var result = await service.GenerateAsync(
            new ImageGenerationCliFallbackRequest(
                "task-generate",
                "codex-worker",
                frozenPrompt,
                promptPath,
                [],
                "1024x1024",
                outputPath),
            CancellationToken.None);

        Equal(outputPath, result.SourcePath, "CLI fallback 必须返回精确 recommendedSourcePath");
        False(result.AlreadyExisted, "新生成的模型原图不能标记为恢复产物");
        Equal(1, runner.Requests.Count, "无参考图任务应只运行一次正式 CLI");
        var launch = runner.Requests[0];
        Equal("C:\\Python312\\python.exe", launch.FileName, "必须使用 probe 返回的绝对 Python");
        SequenceEqual(
            new[]
            {
                scriptPath,
                "generate",
                "--prompt-file",
                promptPath,
                "--size",
                "1024x1024",
                "--output-format",
                "png",
                "--n",
                "1",
                "--no-augment",
                "--out",
                outputPath
            },
            launch.Arguments,
            "CLI 参数必须使用冻结 prompt、精确尺寸与唯一输出路径");
        Equal("test-api-key", launch.EnvironmentVariables["OPENAI_API_KEY"],
            "API key 只能通过子进程环境传递");
        False(launch.Arguments.Any(argument => argument.Contains("test-api-key", StringComparison.Ordinal)),
            "API key 不得出现在命令参数中");
        Equal(frozenPrompt, await File.ReadAllTextAsync(promptPath),
            "prompt 文件必须逐字保存冻结文本");
    }

    private static async Task EditRepeatsReferencesAndPreservesPyLauncherPrefixAsync(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "image-generation-cli-edit", Guid.NewGuid().ToString("N"));
        var promptPath = Path.Combine(root, "20-prompts", "task.prompt.txt");
        var outputPath = Path.Combine(root, "30-model-output", "task.model.png");
        var firstReference = Path.Combine(root, "10-style-reference", "first.png");
        var secondReference = Path.Combine(root, "10-style-reference", "second.png");
        var scriptPath = Path.Combine(root, "codex", "skills", ".system", "imagegen", "scripts", "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(scriptPath, "# smoke-test image_gen.py");
        WritePng(firstReference);
        WritePng(secondReference);

        var probe = new FixedProbe(new ImageGenerationCliFallbackProbeResult(
            new ImageGenerationPythonCommand("C:\\Windows\\py.exe", ["-3"], "C:\\Windows\\py.exe -3"),
            scriptPath,
            "test-api-key",
            "https://example.invalid/v1",
            "C:\\Windows",
            "py -3 已就绪。"));
        var runner = new RecordingRunner(request =>
        {
            WritePng(outputPath);
            return new ImageGenerationCliProcessResult(0, string.Empty, string.Empty, false);
        });
        var service = new ImageGenerationCliFallbackService(probe, runner);

        await service.GenerateAsync(
            new ImageGenerationCliFallbackRequest(
                "task-edit",
                "codex-worker",
                "保持参考图风格并生成一个瓦片。",
                promptPath,
                [firstReference, secondReference],
                "1536x1024",
                outputPath),
            CancellationToken.None);

        var arguments = runner.Requests.Single().Arguments;
        SequenceEqual(
            new[]
            {
                "-3",
                scriptPath,
                "edit",
                "--prompt-file",
                promptPath,
                "--size",
                "1536x1024",
                "--output-format",
                "png",
                "--n",
                "1",
                "--no-augment",
                "--image",
                firstReference,
                "--image",
                secondReference,
                "--out",
                outputPath
            },
            arguments,
            "edit 必须保留 py -3 前缀并按冻结顺序重复 --image");
        Equal("https://example.invalid/v1", runner.Requests.Single().EnvironmentVariables["OPENAI_BASE_URL"],
            "自定义 OpenAI Base URL 只能通过环境传递");
    }

    private static async Task ExistingValidPngSkipsProbeAndImageApiAsync(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "image-generation-cli-existing", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(root, "30-model-output", "task.model.png");
        WritePng(outputPath);
        var service = new ImageGenerationCliFallbackService(new ThrowingProbe(), new ThrowingRunner());

        var result = await service.GenerateAsync(
            new ImageGenerationCliFallbackRequest(
                "task-existing",
                "codex-worker",
                "无需重复生成。",
                Path.Combine(root, "20-prompts", "task.prompt.txt"),
                [],
                "1024x1024",
                outputPath),
            CancellationToken.None);

        True(result.AlreadyExisted, "有效 recommendedSourcePath 必须幂等恢复而不要求 Python/API key");
        False(File.Exists(Path.Combine(root, "20-prompts", "task.prompt.txt")),
            "恢复既有 PNG 时不应创建多余 prompt 文件");
    }

    private static async Task InvalidExistingPngIsRegeneratedAsync(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "image-generation-cli-recover-invalid", Guid.NewGuid().ToString("N"));
        var promptPath = Path.Combine(root, "20-prompts", "task.prompt.txt");
        var outputPath = Path.Combine(root, "30-model-output", "task.model.png");
        var scriptPath = Path.Combine(root, "codex", "skills", ".system", "imagegen", "scripts", "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(outputPath, "partial stale output");
        await File.WriteAllTextAsync(scriptPath, "# smoke-test image_gen.py");
        var probe = CreateFixedProbe(scriptPath);
        var runner = new RecordingRunner(_ =>
        {
            WritePng(outputPath);
            return new ImageGenerationCliProcessResult(0, string.Empty, string.Empty, false);
        });
        var service = new ImageGenerationCliFallbackService(probe, runner);

        var result = await service.GenerateAsync(
            CreateRequest("task-recover-invalid", promptPath, outputPath),
            CancellationToken.None);

        False(result.AlreadyExisted, "损坏或半写入的模型源 PNG 必须允许精确路径重试生成");
        Equal(1, runner.Requests.Count, "损坏模型源文件不得永久阻断后续 Image API 兜底");
        var quarantinedPath = Directory.EnumerateFiles(
                Path.Combine(Path.GetDirectoryName(outputPath)!, ".gassetforge-invalid"),
                "*.invalid",
                SearchOption.TopDirectoryOnly)
            .Single();
        Equal("partial stale output", await File.ReadAllTextAsync(quarantinedPath),
            "损坏/半写入模型源必须隔离保留供诊断，不得直接删除");
        True(result.DiagnosticSummary.Contains(quarantinedPath, StringComparison.Ordinal),
            "成功诊断应回传被隔离中间文件的精确路径");
        using var image = Image.Load<Rgba32>(outputPath);
        Equal(2, image.Width, "重试后应留下可完整解码的真实 PNG");
    }

    private static async Task ConcurrentDuplicateCallsShareSingleGenerationAsync(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "image-generation-cli-concurrent", Guid.NewGuid().ToString("N"));
        var promptPath = Path.Combine(root, "20-prompts", "task.prompt.txt");
        var outputPath = Path.Combine(root, "30-model-output", "task.model.png");
        var scriptPath = Path.Combine(root, "codex", "skills", ".system", "imagegen", "scripts", "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(scriptPath, "# smoke-test image_gen.py");
        var runner = new BlockingPngRunner(outputPath);
        var service = new ImageGenerationCliFallbackService(CreateFixedProbe(scriptPath), runner);
        var request = CreateRequest("task-concurrent", promptPath, outputPath);

        var first = service.GenerateAsync(request, CancellationToken.None);
        await runner.FirstInvocationEntered;
        var second = service.GenerateAsync(request, CancellationToken.None);
        var unexpectedSecondInvocation = await Task.WhenAny(
            runner.SecondInvocationEntered,
            Task.Delay(TimeSpan.FromMilliseconds(200)));
        False(ReferenceEquals(unexpectedSecondInvocation, runner.SecondInvocationEntered),
            "同一 recommendedSourcePath 的并发重入不得启动第二次 Image API 调用");
        runner.Release();

        var results = await Task.WhenAll(first, second);
        Equal(1, runner.InvocationCount, "并发重复调用必须合并为一次真实图片生成");
        Equal(1, results.Count(result => result.AlreadyExisted),
            "等待方应在首个调用落盘后走有效 PNG 幂等恢复");
    }

    private static async Task ProbeFallsBackToPyLauncherWithPython3PrefixAsync(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "image-generation-cli-probe", Guid.NewGuid().ToString("N"));
        var codexHome = Path.Combine(root, "codex-home");
        var scriptPath = Path.Combine(codexHome, "skills", ".system", "imagegen", "scripts", "image_gen.py");
        var localAppData = Path.Combine(root, "local-app-data");
        var pythonPath = Path.Combine(localAppData, "Programs", "Python", "Python312", "python.exe");
        var pyPath = Path.Combine(localAppData, "Programs", "Python", "Launcher", "py.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(pyPath)!);
        await File.WriteAllTextAsync(scriptPath, "# smoke-test image_gen.py");
        await File.WriteAllTextAsync(pythonPath, "python");
        await File.WriteAllTextAsync(pyPath, "py");

        var runner = new RecordingRunner(request =>
            string.Equals(request.FileName, pythonPath, StringComparison.OrdinalIgnoreCase)
                ? throw new Win32Exception("broken python executable")
                : new ImageGenerationCliProcessResult(0, "ok", string.Empty, false));
        string? ReadEnvironment(string name) => name switch
        {
            "CODEX_HOME" => codexHome,
            "OPENAI_API_KEY" => "probe-api-key",
            _ => null
        };
        var probe = new ImageGenerationCliFallbackProbe(
            runner,
            ReadEnvironment,
            () => localAppData,
            () => root,
            () => string.Empty);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Equal(pyPath, result.PythonCommand.ExecutablePath,
            "首个 Python 损坏时必须继续尝试 LocalAppData py launcher");
        SequenceEqual(new[] { "-3" }, result.PythonCommand.PrefixArguments,
            "py launcher 必须固定选择 Python 3");
        Equal(4, runner.Requests.Count,
            "探针应记录失败 python --version，并让 py -3 依次验证版本、脚本与 SDK");
        True(runner.Requests.Skip(1).All(request => request.Arguments.FirstOrDefault() == "-3"),
            "所有 py launcher 探针都必须保留 -3 前缀");
    }

    private static async Task LocalAppDataPythonUsesSemanticVersionOrderAsync(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "image-generation-cli-version-order", Guid.NewGuid().ToString("N"));
        var codexHome = Path.Combine(root, "codex-home");
        var scriptPath = Path.Combine(codexHome, "skills", ".system", "imagegen", "scripts", "image_gen.py");
        var localAppData = Path.Combine(root, "local-app-data");
        var python39 = Path.Combine(localAppData, "Programs", "Python", "Python39", "python.exe");
        var python312 = Path.Combine(localAppData, "Programs", "Python", "Python312", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(python39)!);
        Directory.CreateDirectory(Path.GetDirectoryName(python312)!);
        await File.WriteAllTextAsync(scriptPath, "# smoke-test image_gen.py");
        await File.WriteAllTextAsync(python39, "python 3.9");
        await File.WriteAllTextAsync(python312, "python 3.12");

        var runner = new RecordingRunner(_ =>
            new ImageGenerationCliProcessResult(0, "ok", string.Empty, false));
        string? ReadEnvironment(string name) => name switch
        {
            "CODEX_HOME" => codexHome,
            "OPENAI_API_KEY" => "probe-api-key",
            _ => null
        };
        var probe = new ImageGenerationCliFallbackProbe(
            runner,
            ReadEnvironment,
            () => localAppData,
            () => root,
            () => string.Empty);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Equal(python312, result.PythonCommand.ExecutablePath,
            "LocalAppData Python 候选必须按 3.12 高于 3.9 的语义版本顺序探测");
        True(runner.Requests.Take(3).All(request =>
                string.Equals(request.FileName, python312, StringComparison.OrdinalIgnoreCase)),
            "首个版本、脚本和 SDK 探针都必须使用最高语义版本 Python");
    }

    private static async Task CliFailureRedactsApiKeyAsync(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "image-generation-cli-redaction", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(root, "30-model-output", "task.model.png");
        var scriptPath = Path.Combine(root, "codex", "skills", ".system", "imagegen", "scripts", "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(scriptPath, "# smoke-test image_gen.py");
        const string apiKey = "sk-test-secret-12345678";
        var probe = new FixedProbe(new ImageGenerationCliFallbackProbeResult(
            new ImageGenerationPythonCommand("C:\\Python312\\python.exe", [], "C:\\Python312\\python.exe"),
            scriptPath,
            apiKey,
            null,
            "C:\\Python312",
            "ready"));
        var runner = new RecordingRunner(_ =>
            new ImageGenerationCliProcessResult(1, string.Empty, $"Authorization: Bearer {apiKey}", false));
        var service = new ImageGenerationCliFallbackService(probe, runner);

        var exception = await ThrowsAsync<ImageGenerationCliFallbackException>(() => service.GenerateAsync(
            new ImageGenerationCliFallbackRequest(
                "task-redaction",
                "codex-worker",
                "测试脱敏。",
                Path.Combine(root, "20-prompts", "task.prompt.txt"),
                [],
                "1024x1024",
                outputPath),
            CancellationToken.None));

        False(exception.Message.Contains(apiKey, StringComparison.Ordinal),
            "CLI 失败日志不得泄露 API key");
        True(exception.Message.Contains("[REDACTED]", StringComparison.Ordinal),
            "CLI 失败日志应明确标记已脱敏内容");
        False(runner.Requests.Single().Arguments.Any(value => value.Contains(apiKey, StringComparison.Ordinal)),
            "失败路径同样不得把 API key 放进参数");
    }

    private static async Task NetworkEnvironmentIsForwardedAndRedactedAsync(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "image-generation-cli-network-environment", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(root, "30-model-output", "task.model.png");
        var scriptPath = Path.Combine(root, "codex", "skills", ".system", "imagegen", "scripts", "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(scriptPath, "# smoke-test image_gen.py");
        const string apiKey = "sk-network-secret-12345678";
        const string baseUrl = "https://base-user:base-secret@example.invalid/v1";
        const string httpsProxy = "http://proxy-user:proxy-secret@127.0.0.1:7890";
        var probe = new FixedProbe(new ImageGenerationCliFallbackProbeResult(
            new ImageGenerationPythonCommand("C:\\Python312\\python.exe", [], "C:\\Python312\\python.exe"),
            scriptPath,
            apiKey,
            baseUrl,
            "C:\\Python312",
            "ready")
        {
            ForwardedEnvironmentVariables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["HTTPS_PROXY"] = httpsProxy,
                ["NO_PROXY"] = "127.0.0.1,localhost"
            }
        });
        var runner = new RecordingRunner(_ => new ImageGenerationCliProcessResult(
            1,
            string.Empty,
            $"key={apiKey}; base={baseUrl}; proxy={httpsProxy}",
            false));
        var service = new ImageGenerationCliFallbackService(probe, runner);

        var exception = await ThrowsAsync<ImageGenerationCliFallbackException>(() => service.GenerateAsync(
            CreateRequest(
                "task-network-environment",
                Path.Combine(root, "20-prompts", "task.prompt.txt"),
                outputPath),
            CancellationToken.None));

        var environment = runner.Requests.Single().EnvironmentVariables;
        Equal(baseUrl, environment["OPENAI_BASE_URL"], "自定义 Base URL 必须显式传给图片 CLI 子进程");
        Equal(httpsProxy, environment["HTTPS_PROXY"], "HTTPS 代理必须显式传给图片 CLI 子进程");
        Equal("127.0.0.1,localhost", environment["NO_PROXY"], "代理绕过规则必须显式传给图片 CLI 子进程");
        False(exception.Message.Contains(apiKey, StringComparison.Ordinal), "失败日志不得泄露 API key");
        False(exception.Message.Contains(baseUrl, StringComparison.Ordinal), "失败日志不得泄露 Base URL 中的凭据");
        False(exception.Message.Contains(httpsProxy, StringComparison.Ordinal), "失败日志不得泄露代理 URL 中的凭据");
    }

    private static async Task SuccessfulExitWithoutValidPngIsRejectedAsync(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "image-generation-cli-invalid", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(root, "30-model-output", "task.model.png");
        var scriptPath = Path.Combine(root, "codex", "skills", ".system", "imagegen", "scripts", "image_gen.py");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(scriptPath, "# smoke-test image_gen.py");
        var probe = new FixedProbe(new ImageGenerationCliFallbackProbeResult(
            new ImageGenerationPythonCommand("C:\\Python312\\python.exe", [], "C:\\Python312\\python.exe"),
            scriptPath,
            "test-api-key",
            null,
            "C:\\Python312",
            "ready"));
        var runner = new RecordingRunner(_ =>
        {
            File.WriteAllText(outputPath, "not a png");
            return new ImageGenerationCliProcessResult(0, $"Wrote {outputPath}", string.Empty, false);
        });
        var service = new ImageGenerationCliFallbackService(probe, runner);

        await ThrowsAsync<ImageGenerationCliFallbackException>(() => service.GenerateAsync(
            new ImageGenerationCliFallbackRequest(
                "task-invalid",
                "codex-worker",
                "测试无效 PNG。",
                Path.Combine(root, "20-prompts", "task.prompt.txt"),
                [],
                "1024x1024",
                outputPath),
            CancellationToken.None));
    }

    private static void WritePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(2, 2, new Rgba32(255, 0, 255, 255));
        image.SaveAsPng(path);
    }

    private static FixedProbe CreateFixedProbe(string scriptPath) =>
        new(new ImageGenerationCliFallbackProbeResult(
            new ImageGenerationPythonCommand("C:\\Python312\\python.exe", [], "C:\\Python312\\python.exe"),
            scriptPath,
            "test-api-key",
            null,
            "C:\\Python312",
            "ready"));

    private static ImageGenerationCliFallbackRequest CreateRequest(
        string taskId,
        string promptPath,
        string outputPath) =>
        new(
            taskId,
            "codex-worker",
            "生成一个测试瓦片。",
            promptPath,
            [],
            "1024x1024",
            outputPath);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}。Expected={expected}; Actual={actual}");
        }
    }

    private static void False(bool value, string message)
    {
        if (value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void SequenceEqual(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"{message}。Expected=[{string.Join(", ", expected)}]; Actual=[{string.Join(", ", actual)}]");
        }
    }

    private sealed class FixedProbe(ImageGenerationCliFallbackProbeResult result)
        : IImageGenerationCliFallbackProbe
    {
        public Task<ImageGenerationCliFallbackProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingProbe : IImageGenerationCliFallbackProbe
    {
        public Task<ImageGenerationCliFallbackProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("既有有效 PNG 不应执行 probe。");
    }

    private sealed class ThrowingRunner : IImageGenerationCliProcessRunner
    {
        public Task<ImageGenerationCliProcessResult> RunAsync(
            ImageGenerationCliProcessRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("既有有效 PNG 不应执行 Image API。");
    }

    private sealed class RecordingRunner(
        Func<ImageGenerationCliProcessRequest, ImageGenerationCliProcessResult> handler)
        : IImageGenerationCliProcessRunner
    {
        internal List<ImageGenerationCliProcessRequest> Requests { get; } = [];

        public Task<ImageGenerationCliProcessResult> RunAsync(
            ImageGenerationCliProcessRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }

    private sealed class BlockingPngRunner(string outputPath) : IImageGenerationCliProcessRunner
    {
        private readonly TaskCompletionSource _firstInvocationEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondInvocationEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocationCount;

        internal Task FirstInvocationEntered => _firstInvocationEntered.Task;

        internal Task SecondInvocationEntered => _secondInvocationEntered.Task;

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        internal void Release() => _release.TrySetResult();

        public async Task<ImageGenerationCliProcessResult> RunAsync(
            ImageGenerationCliProcessRequest request,
            CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _invocationCount);
            if (count == 1)
            {
                _firstInvocationEntered.TrySetResult();
            }
            else
            {
                _secondInvocationEntered.TrySetResult();
            }

            await _release.Task.WaitAsync(cancellationToken);
            WritePng(outputPath);
            return new ImageGenerationCliProcessResult(0, string.Empty, string.Empty, false);
        }
    }
}
