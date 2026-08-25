using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GAssetForge.McpServer;

internal interface IImageGenerationCliFallbackService
{
    Task<ImageGenerationCliFallbackResult> GenerateAsync(
        ImageGenerationCliFallbackRequest request,
        CancellationToken cancellationToken);
}

internal sealed record ImageGenerationCliFallbackRequest(
    string TaskId,
    string AgentId,
    string PromptText,
    string PromptFilePath,
    IReadOnlyList<string> ReferenceSourcePaths,
    string CliSize,
    string RecommendedSourcePath);

internal sealed record ImageGenerationCliFallbackResult(
    string SourcePath,
    string PythonCommandDisplay,
    string ImageGenScriptPath,
    string DiagnosticSummary,
    bool AlreadyExisted);

internal sealed record ImageGenerationPythonCommand(
    string ExecutablePath,
    IReadOnlyList<string> PrefixArguments,
    string DisplayText);

internal sealed record ImageGenerationCliFallbackProbeResult(
    ImageGenerationPythonCommand PythonCommand,
    string ImageGenScriptPath,
    string ApiKey,
    string? OpenAiBaseUrl,
    string FreshPathEnvironment,
    string DiagnosticSummary)
{
    internal IReadOnlyDictionary<string, string?> ForwardedEnvironmentVariables { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

internal interface IImageGenerationCliFallbackProbe
{
    Task<ImageGenerationCliFallbackProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

internal sealed record ImageGenerationCliProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> EnvironmentVariables,
    TimeSpan Timeout);

internal sealed record ImageGenerationCliProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

internal sealed record ImageGenerationCliOutputCapture(
    string StandardOutput,
    string StandardError,
    bool DrainTimedOut);

internal interface IImageGenerationCliProcessRunner
{
    Task<ImageGenerationCliProcessResult> RunAsync(
        ImageGenerationCliProcessRequest request,
        CancellationToken cancellationToken);
}

internal sealed class ImageGenerationCliFallbackException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

internal sealed class ImageGenerationCliFallbackService : IImageGenerationCliFallbackService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex CliSizePattern = new(
        @"^(?:auto|[1-9][0-9]{0,4}x[1-9][0-9]{0,4})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly TimeSpan DefaultGenerationTimeout = TimeSpan.FromMinutes(8);

    private readonly IImageGenerationCliFallbackProbe _probe;
    private readonly IImageGenerationCliProcessRunner _runner;
    private readonly TimeSpan _generationTimeout;

    internal ImageGenerationCliFallbackService()
        : this(null, null, null)
    {
    }

    internal ImageGenerationCliFallbackService(
        IImageGenerationCliFallbackProbe? probe,
        IImageGenerationCliProcessRunner? runner,
        TimeSpan? generationTimeout = null)
    {
        _runner = runner ?? new SystemImageGenerationCliProcessRunner();
        _probe = probe ?? new ImageGenerationCliFallbackProbe(_runner);
        _generationTimeout = generationTimeout ?? DefaultGenerationTimeout;
        if (_generationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(generationTimeout));
        }
    }

    public async Task<ImageGenerationCliFallbackResult> GenerateAsync(
        ImageGenerationCliFallbackRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = NormalizeRequest(request);
        await using var outputLease = await AcquireOutputLeaseAsync(
                normalized.RecommendedSourcePath,
                cancellationToken)
            .ConfigureAwait(false);
        return await GenerateCoreAsync(normalized, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FileStream> AcquireOutputLeaseAsync(
        string recommendedSourcePath,
        CancellationToken cancellationToken)
    {
        var lockPath = recommendedSourcePath + ".gassetforge-imagegen.lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(lockPath) &&
                    (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ImageGenerationCliFallbackException(
                        $"图片 CLI 互斥锁文件不能是符号链接或重解析点：{lockPath}");
                }

                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (ImageGenerationCliFallbackException)
            {
                throw;
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new ImageGenerationCliFallbackException(
                    $"无法创建图片 CLI 互斥锁文件：{lockPath}；{exception.Message}",
                    exception);
            }
        }
    }

    private async Task<ImageGenerationCliFallbackResult> GenerateCoreAsync(
        NormalizedRequest normalized,
        CancellationToken cancellationToken)
    {
        string? quarantinedInvalidOutputPath = null;
        if (File.Exists(normalized.RecommendedSourcePath))
        {
            try
            {
                EnsureUsablePng(normalized.RecommendedSourcePath, "既有 recommendedSourcePath");
                return new ImageGenerationCliFallbackResult(
                    normalized.RecommendedSourcePath,
                    "未调用（已有有效 PNG）",
                    string.Empty,
                    "精确 recommendedSourcePath 已有可完整解码的 PNG；未重复调用 Image API。",
                    AlreadyExisted: true);
            }
            catch (ImageGenerationCliFallbackException)
            {
                quarantinedInvalidOutputPath = QuarantineInvalidOutputForRetry(
                    normalized.RecommendedSourcePath);
            }
        }

        await EnsureFrozenPromptFileAsync(normalized, cancellationToken).ConfigureAwait(false);
        var probe = await _probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        ValidateProbeResult(probe);

        var arguments = BuildArguments(normalized, probe);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in probe.ForwardedEnvironmentVariables)
        {
            environment[variable.Key] = variable.Value;
        }

        environment["PATH"] = probe.FreshPathEnvironment;
        environment["OPENAI_API_KEY"] = probe.ApiKey;
        environment["OPENAI_BASE_URL"] = probe.OpenAiBaseUrl;
        environment["PYTHONUTF8"] = "1";
        environment["PYTHONIOENCODING"] = "utf-8";

        var processRequest = new ImageGenerationCliProcessRequest(
            probe.PythonCommand.ExecutablePath,
            arguments,
            Path.GetDirectoryName(normalized.PromptFilePath)!,
            environment,
            _generationTimeout);
        ImageGenerationCliProcessResult processResult;
        try
        {
            processResult = await _runner.RunAsync(processRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ImageGenerationCliFallbackException(
                $"图片 CLI 兜底无法启动 {probe.PythonCommand.DisplayText}：" +
                SanitizeDiagnostic(exception.Message, SensitiveValues(probe)),
                exception);
        }

        if (processResult.TimedOut || processResult.ExitCode != 0)
        {
            var reason = processResult.TimedOut
                ? $"执行超过 {_generationTimeout.TotalSeconds:0} 秒并已终止进程树"
                : $"进程退出码为 {processResult.ExitCode}";
            var detail = JoinProcessDiagnostics(processResult, probe);
            throw new ImageGenerationCliFallbackException(
                $"图片 CLI 兜底失败（task {ShortId(normalized.TaskId)}，{reason}）：{detail}");
        }

        EnsureUsablePng(normalized.RecommendedSourcePath, "image_gen.py 输出");
        return new ImageGenerationCliFallbackResult(
            normalized.RecommendedSourcePath,
            probe.PythonCommand.DisplayText,
            probe.ImageGenScriptPath,
            $"官方 image_gen.py 已通过 {probe.PythonCommand.DisplayText} 生成并验证精确 PNG。" +
            " " +
            (quarantinedInvalidOutputPath is null
                ? string.Empty
                : $"已将本任务精确路径中的损坏/半写入旧文件隔离到 {quarantinedInvalidOutputPath} 后重试。 ") +
            SanitizeDiagnostic(probe.DiagnosticSummary, SensitiveValues(probe)),
            AlreadyExisted: false);
    }

    private static string QuarantineInvalidOutputForRetry(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ImageGenerationCliFallbackException(
                    $"损坏的 recommendedSourcePath 是符号链接或重解析点，拒绝自动隔离：{path}");
            }

            var parentDirectory = Path.GetDirectoryName(path)
                ?? throw new InvalidDataException("recommendedSourcePath 缺少父目录。");
            var quarantineDirectory = Path.Combine(parentDirectory, ".gassetforge-invalid");
            Directory.CreateDirectory(quarantineDirectory);
            if ((File.GetAttributes(quarantineDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ImageGenerationCliFallbackException(
                    $"损坏模型源隔离目录不能是符号链接或重解析点：{quarantineDirectory}");
            }

            var quarantinePath = Path.Combine(
                quarantineDirectory,
                $"{Path.GetFileName(path)}.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.{Guid.NewGuid():N}.invalid");
            File.Move(path, quarantinePath);
            return quarantinePath;
        }
        catch (ImageGenerationCliFallbackException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
                   IOException or
                   UnauthorizedAccessException or
                   NotSupportedException)
        {
            throw new ImageGenerationCliFallbackException(
                $"无法隔离本任务精确 recommendedSourcePath 中的损坏/半写入文件：{path}；{exception.Message}",
                exception);
        }
    }

    private static NormalizedRequest NormalizeRequest(ImageGenerationCliFallbackRequest request)
    {
        var taskId = RequireText(request.TaskId, nameof(request.TaskId), 256);
        var agentId = RequireText(request.AgentId, nameof(request.AgentId), 128);
        var promptText = RequirePromptText(request.PromptText);
        var promptFilePath = NormalizeAbsolutePath(request.PromptFilePath, nameof(request.PromptFilePath));
        var sourcePath = NormalizeAbsolutePath(
            request.RecommendedSourcePath,
            nameof(request.RecommendedSourcePath));
        if (!string.Equals(Path.GetExtension(sourcePath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("recommendedSourcePath 必须使用 .png 扩展名。", nameof(request));
        }

        var cliSize = RequireText(request.CliSize, nameof(request.CliSize), 32);
        if (!CliSizePattern.IsMatch(cliSize))
        {
            throw new ArgumentException("cliSize 必须是 auto 或 WIDTHxHEIGHT。", nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.ReferenceSourcePaths);
        var references = request.ReferenceSourcePaths
            .Select((path, index) => NormalizeAbsolutePath(path, $"ReferenceSourcePaths[{index}]"))
            .ToArray();
        foreach (var reference in references)
        {
            if (!File.Exists(reference))
            {
                throw new FileNotFoundException("冻结风格参考图不存在。", reference);
            }

            if ((File.GetAttributes(reference) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"冻结风格参考图不能是符号链接或重解析点：{reference}");
            }
        }

        var outputDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidDataException("recommendedSourcePath 缺少父目录。");
        if (!Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException($"recommendedSourcePath 父目录不存在：{outputDirectory}");
        }

        return new NormalizedRequest(
            taskId,
            agentId,
            promptText,
            promptFilePath,
            references,
            cliSize,
            sourcePath);
    }

    private static async Task EnsureFrozenPromptFileAsync(
        NormalizedRequest request,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(request.PromptFilePath)
            ?? throw new InvalidDataException("PromptFilePath 缺少父目录。");
        Directory.CreateDirectory(directory);
        if (File.Exists(request.PromptFilePath))
        {
            if ((File.GetAttributes(request.PromptFilePath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("冻结 prompt 文件不能是符号链接或重解析点。");
            }

            var existing = await File.ReadAllTextAsync(request.PromptFilePath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(existing, request.PromptText, StringComparison.Ordinal))
            {
                throw new InvalidDataException("冻结 prompt 文件已存在但内容与任务快照不一致。");
            }

            return;
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(request.PromptFilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    request.PromptText,
                    Utf8NoBom,
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, request.PromptFilePath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static IReadOnlyList<string> BuildArguments(
        NormalizedRequest request,
        ImageGenerationCliFallbackProbeResult probe)
    {
        var arguments = new List<string>(probe.PythonCommand.PrefixArguments.Count + 18)
        {
        };
        arguments.AddRange(probe.PythonCommand.PrefixArguments);
        arguments.Add(probe.ImageGenScriptPath);
        arguments.Add(request.ReferenceSourcePaths.Count == 0 ? "generate" : "edit");
        arguments.Add("--prompt-file");
        arguments.Add(request.PromptFilePath);
        arguments.Add("--size");
        arguments.Add(request.CliSize);
        arguments.Add("--output-format");
        arguments.Add("png");
        arguments.Add("--n");
        arguments.Add("1");
        arguments.Add("--no-augment");
        foreach (var reference in request.ReferenceSourcePaths)
        {
            arguments.Add("--image");
            arguments.Add(reference);
        }

        arguments.Add("--out");
        arguments.Add(request.RecommendedSourcePath);
        return arguments;
    }

    private static void ValidateProbeResult(ImageGenerationCliFallbackProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (!Path.IsPathFullyQualified(probe.PythonCommand.ExecutablePath) ||
            string.IsNullOrWhiteSpace(probe.PythonCommand.DisplayText))
        {
            throw new ImageGenerationCliFallbackException("图片 CLI 预检没有返回有效的 Python 启动命令。");
        }

        if (!Path.IsPathFullyQualified(probe.ImageGenScriptPath) ||
            !File.Exists(probe.ImageGenScriptPath))
        {
            throw new ImageGenerationCliFallbackException("图片 CLI 预检返回的 image_gen.py 不存在。");
        }

        if (string.IsNullOrWhiteSpace(probe.ApiKey))
        {
            throw new ImageGenerationCliFallbackException("图片 CLI 兜底需要 OPENAI_API_KEY。");
        }
    }

    private static void EnsureUsablePng(string path, string label)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= PngSignature.Length)
            {
                throw new InvalidDataException($"{label} 未产生非空 PNG 文件：{path}");
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"{label} 不能是符号链接或重解析点：{path}");
            }

            using var stream = info.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> signature = stackalloc byte[PngSignature.Length];
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual(PngSignature))
            {
                throw new InvalidDataException($"{label} 的 PNG 签名无效：{path}");
            }

            stream.Position = 0;
            using var image = Image.Load<Rgba32>(stream);
            if (image.Width <= 0 || image.Height <= 0)
            {
                throw new InvalidDataException($"{label} 的画布尺寸无效：{path}");
            }
        }
        catch (ImageGenerationCliFallbackException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
                   IOException or
                   UnauthorizedAccessException or
                   InvalidDataException or
                   InvalidImageContentException or
                   UnknownImageFormatException or
                   NotSupportedException)
        {
            throw new ImageGenerationCliFallbackException(
                $"{label} 无法完整解码：{SanitizeDiagnostic(exception.Message, null)}",
                exception);
        }
    }

    private static string JoinProcessDiagnostics(
        ImageGenerationCliProcessResult result,
        ImageGenerationCliFallbackProbeResult probe)
    {
        var combined = string.Join(
            " | ",
            new[] { result.StandardError, result.StandardOutput }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => SanitizeDiagnostic(value, SensitiveValues(probe))));
        return string.IsNullOrWhiteSpace(combined) ? "进程未返回诊断文本" : combined;
    }

    private static IReadOnlyList<string?> SensitiveValues(
        ImageGenerationCliFallbackProbeResult probe) =>
        new[] { probe.ApiKey, probe.OpenAiBaseUrl }
            .Concat(probe.ForwardedEnvironmentVariables.Values)
            .ToArray();

    private static string SanitizeDiagnostic(
        string value,
        IEnumerable<string?>? sensitiveValues)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        foreach (var sensitiveValue in (sensitiveValues ?? [])
                     .Where(item => !string.IsNullOrEmpty(item))
                     .Select(item => item!)
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(item => item.Length))
        {
            sanitized = sanitized.Replace(sensitiveValue, "[REDACTED]", StringComparison.Ordinal);
        }

        sanitized = Regex.Replace(
            sanitized,
            @"\bsk-[A-Za-z0-9_-]{8,}\b",
            "[REDACTED]",
            RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(
            sanitized,
            @"\b(?<scheme>[A-Za-z][A-Za-z0-9+.-]*://)[^\s/:@]+:[^\s/@]+@",
            "${scheme}[REDACTED]@",
            RegexOptions.CultureInvariant);
        return sanitized.Length <= 2_000 ? sanitized : sanitized[..2_000] + "…";
    }

    private static string RequireText(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} 不能为空。", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException($"{parameterName} 超过最大长度 {maximumLength}。", parameterName);
        }

        return normalized;
    }

    private static string RequirePromptText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("PromptText 不能为空。", nameof(value));
        }

        if (value.Length > 256_000)
        {
            throw new ArgumentException("PromptText 超过最大长度 256000。", nameof(value));
        }

        return value;
    }

    private static string NormalizeAbsolutePath(string? value, string parameterName)
    {
        var text = RequireText(value, parameterName, 32_767);
        if (!Path.IsPathFullyQualified(text))
        {
            throw new ArgumentException($"{parameterName} 必须是绝对路径。", parameterName);
        }

        return Path.GetFullPath(text);
    }

    private static string ShortId(string value) => value.Length <= 12 ? value : value[..12];

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record NormalizedRequest(
        string TaskId,
        string AgentId,
        string PromptText,
        string PromptFilePath,
        IReadOnlyList<string> ReferenceSourcePaths,
        string CliSize,
        string RecommendedSourcePath);
}

internal sealed class ImageGenerationCliFallbackProbe : IImageGenerationCliFallbackProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] ForwardedNetworkEnvironmentNames =
    [
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy",
        "no_proxy",
        "OPENAI_ORG_ID",
        "OPENAI_PROJECT_ID",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        "REQUESTS_CA_BUNDLE"
    ];
    private readonly IImageGenerationCliProcessRunner _runner;
    private readonly Func<string, string?> _environmentProvider;
    private readonly Func<string?> _localAppDataProvider;
    private readonly Func<string?> _userProfileProvider;
    private readonly Func<string> _freshPathProvider;

    internal ImageGenerationCliFallbackProbe(
        IImageGenerationCliProcessRunner runner,
        Func<string, string?>? environmentProvider = null,
        Func<string?>? localAppDataProvider = null,
        Func<string?>? userProfileProvider = null,
        Func<string>? freshPathProvider = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _environmentProvider = environmentProvider ?? ReadFreshEnvironmentVariable;
        _localAppDataProvider = localAppDataProvider ??
                                (() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _userProfileProvider = userProfileProvider ??
                               (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _freshPathProvider = freshPathProvider ?? BuildFreshPathEnvironment;
    }

    public async Task<ImageGenerationCliFallbackProbeResult> ProbeAsync(
        CancellationToken cancellationToken)
    {
        var imageGenScriptPath = ResolveImageGenScriptPath();
        if (!File.Exists(imageGenScriptPath))
        {
            throw new ImageGenerationCliFallbackException(
                $"未找到官方 imagegen skill 脚本：{imageGenScriptPath}");
        }

        var freshPath = _freshPathProvider();
        var diagnostics = new List<string>();
        ImageGenerationPythonCommand? selected = null;
        foreach (var candidate in ResolvePythonCandidates(freshPath, diagnostics))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ProbeCommandAsync(candidate, ["--version"], freshPath, cancellationToken)
                    .ConfigureAwait(false))
            {
                diagnostics.Add($"{candidate.DisplayText} 无法启动");
                continue;
            }

            if (!await ProbeCommandAsync(
                    candidate,
                    [imageGenScriptPath, "--help"],
                    freshPath,
                    cancellationToken)
                    .ConfigureAwait(false))
            {
                diagnostics.Add($"{candidate.DisplayText} 无法启动官方 image_gen.py");
                continue;
            }

            if (!await ProbeCommandAsync(
                    candidate,
                    ["-c", "from openai import OpenAI; print('openai-sdk-ready')"],
                    freshPath,
                    cancellationToken)
                    .ConfigureAwait(false))
            {
                diagnostics.Add($"{candidate.DisplayText} 无法导入 OpenAI SDK");
                continue;
            }

            selected = candidate;
            break;
        }

        if (selected is null)
        {
            throw new ImageGenerationCliFallbackException(
                "未找到可执行官方 image_gen.py 且能导入 OpenAI SDK 的 Python：" +
                (diagnostics.Count == 0 ? "没有候选" : string.Join("；", diagnostics.Take(8))));
        }

        var apiKey = _environmentProvider("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ImageGenerationCliFallbackException(
                "图片 CLI 兜底需要 OPENAI_API_KEY；Codex/ChatGPT 登录不能替代 Image API key。");
        }

        var forwardedEnvironment = ForwardedNetworkEnvironmentNames
            .Select(name => new KeyValuePair<string, string?>(name, _environmentProvider(name)))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value,
                StringComparer.OrdinalIgnoreCase);
        return new ImageGenerationCliFallbackProbeResult(
            selected,
            imageGenScriptPath,
            apiKey,
            _environmentProvider("OPENAI_BASE_URL"),
            freshPath,
            diagnostics.Count == 0
                ? "Python、官方 image_gen.py 与 OpenAI SDK 本地探针通过；Image API 网络尚未验证。"
                : "已跳过不可用候选：" + string.Join("；", diagnostics.Take(8)))
        {
            ForwardedEnvironmentVariables = forwardedEnvironment
        };
    }

    private async Task<bool> ProbeCommandAsync(
        ImageGenerationPythonCommand command,
        IReadOnlyList<string> arguments,
        string freshPath,
        CancellationToken cancellationToken)
    {
        var combinedArguments = command.PrefixArguments.Concat(arguments).ToArray();
        try
        {
            var result = await _runner.RunAsync(
                    new ImageGenerationCliProcessRequest(
                        command.ExecutablePath,
                        combinedArguments,
                        Environment.CurrentDirectory,
                        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["PATH"] = freshPath
                        },
                        ProbeTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            return !result.TimedOut && result.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A stale or inaccessible executable must not prevent later candidates (for example
            // the Windows py launcher) from being probed in the same managed process.
            return false;
        }
    }

    private string ResolveImageGenScriptPath()
    {
        var codexHome = _environmentProvider("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            var profile = _userProfileProvider();
            if (string.IsNullOrWhiteSpace(profile))
            {
                throw new ImageGenerationCliFallbackException("无法解析用户目录与 CODEX_HOME。");
            }

            codexHome = Path.Combine(profile, ".codex");
        }

        return Path.GetFullPath(Path.Combine(
            codexHome,
            "skills",
            ".system",
            "imagegen",
            "scripts",
            "image_gen.py"));
    }

    private IEnumerable<ImageGenerationPythonCommand> ResolvePythonCandidates(
        string freshPath,
        ICollection<string> diagnostics)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        var configured = _environmentProvider("GASSETFORGE_PYTHON_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (TryCreateCandidate(configured, seen, out var candidate))
            {
                yield return candidate;
            }
            else
            {
                diagnostics.Add("GASSETFORGE_PYTHON_EXECUTABLE 指向不存在或无效的文件");
            }
        }

        var localAppData = _localAppDataProvider();
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var pythonRoot = Path.Combine(localAppData, "Programs", "Python");
            if (Directory.Exists(pythonRoot))
            {
                IEnumerable<string> directories;
                try
                {
                    directories = Directory.EnumerateDirectories(pythonRoot, "Python*")
                        .OrderByDescending(path => ParsePythonDirectoryVersion(
                            Path.GetFileName(path)))
                        .ThenByDescending(
                            path => Path.GetFileName(path),
                            StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add("无法枚举 LocalAppData Python：" + exception.Message);
                    directories = [];
                }

                foreach (var directory in directories)
                {
                    if (TryCreateCandidate(Path.Combine(directory, "python.exe"), seen, out var candidate))
                    {
                        yield return candidate;
                    }
                }

                if (TryCreateCandidate(
                        Path.Combine(pythonRoot, "Launcher", "py.exe"),
                        seen,
                        out var launcher))
                {
                    yield return launcher;
                }
            }
        }

        foreach (var directory in SplitPath(freshPath))
        {
            var fileNames = OperatingSystem.IsWindows()
                ? new[] { "python.exe", "python3.exe", "py.exe" }
                : new[] { "python3", "python", "py" };
            foreach (var fileName in fileNames)
            {
                if (TryCreateCandidate(Path.Combine(directory, fileName), seen, out var candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static bool TryCreateCandidate(
        string rawPath,
        ISet<string> seen,
        out ImageGenerationPythonCommand command)
    {
        command = null!;
        try
        {
            var trimmed = rawPath.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(trimmed);
            if (!File.Exists(fullPath))
            {
                return false;
            }

            var isLauncher = string.Equals(
                Path.GetFileName(fullPath),
                OperatingSystem.IsWindows() ? "py.exe" : "py",
                StringComparison.OrdinalIgnoreCase);
            var prefix = isLauncher ? new[] { "-3" } : [];
            var key = fullPath + "\0" + string.Join("\0", prefix);
            if (!seen.Add(key))
            {
                return false;
            }

            command = new ImageGenerationPythonCommand(
                fullPath,
                prefix,
                QuoteForDisplay(fullPath) + (isLauncher ? " -3" : string.Empty));
            return true;
        }
        catch (Exception exception) when (exception is
                   ArgumentException or
                   NotSupportedException or
                   PathTooLongException)
        {
            return false;
        }
    }

    private static IEnumerable<string> SplitPath(string pathValue) =>
        pathValue.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim().Trim('"'))
            .Where(value => value.Length > 0);

    private static int ParsePythonDirectoryVersion(string directoryName)
    {
        var digits = new string(directoryName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var version) ? version : -1;
    }

    private static string BuildFreshPathEnvironment()
    {
        var values = new List<string?> { Environment.GetEnvironmentVariable("PATH") };
        if (OperatingSystem.IsWindows())
        {
            try
            {
                values.Add(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine));
                values.Add(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User));
            }
            catch (Exception exception) when (exception is
                       System.Security.SecurityException or
                       UnauthorizedAccessException)
            {
            }
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return string.Join(
            Path.PathSeparator,
            values.Where(value => !string.IsNullOrWhiteSpace(value))
                .SelectMany(value => SplitPath(value!))
                .Distinct(comparer));
    }

    private static string? ReadFreshEnvironmentVariable(string name)
    {
        var processValue = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(processValue) || !OperatingSystem.IsWindows())
        {
            return processValue;
        }

        try
        {
            return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ??
                   Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        }
        catch (Exception exception) when (exception is
                   System.Security.SecurityException or
                   UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string QuoteForDisplay(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}

internal sealed class SystemImageGenerationCliProcessRunner : IImageGenerationCliProcessRunner
{
    private const int MaximumCapturedCharacters = 64 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan DefaultOutputDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _outputDrainTimeout;

    internal SystemImageGenerationCliProcessRunner(TimeSpan? outputDrainTimeout = null)
    {
        _outputDrainTimeout = outputDrainTimeout ?? DefaultOutputDrainTimeout;
        if (_outputDrainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(outputDrainTimeout));
        }
    }

    public async Task<ImageGenerationCliProcessResult> RunAsync(
        ImageGenerationCliProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var variable in request.EnvironmentVariables)
        {
            if (variable.Value is null)
            {
                startInfo.Environment.Remove(variable.Key);
            }
            else
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new Win32Exception("操作系统未启动图片 CLI 子进程。");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new ImageGenerationCliFallbackException(
                $"无法启动图片 CLI 子进程 {request.FileName}：{exception.Message}",
                exception);
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput);
        var stderrTask = ReadBoundedAsync(process.StandardError);
        var timedOut = false;
        var canceled = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
            await WaitAfterKillAsync(process).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            TryKill(process);
            await WaitAfterKillAsync(process).ConfigureAwait(false);
        }

        var capture = await CaptureOutputAsync(
                stdoutTask,
                stderrTask,
                () => CloseOutputReaders(process),
                _outputDrainTimeout)
            .ConfigureAwait(false);
        if (canceled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }

        return new ImageGenerationCliProcessResult(
            timedOut ? -1 : process.ExitCode,
            capture.StandardOutput,
            capture.StandardError,
            timedOut);
    }

    internal static async Task<ImageGenerationCliOutputCapture> CaptureOutputAsync(
        Task<string> stdoutTask,
        Task<string> stderrTask,
        Action closeReaders,
        TimeSpan drainTimeout)
    {
        ArgumentNullException.ThrowIfNull(stdoutTask);
        ArgumentNullException.ThrowIfNull(stderrTask);
        ArgumentNullException.ThrowIfNull(closeReaders);
        if (drainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));
        }

        var combined = Task.WhenAll(stdoutTask, stderrTask);
        if (await Task.WhenAny(combined, Task.Delay(drainTimeout)).ConfigureAwait(false) == combined)
        {
            return new ImageGenerationCliOutputCapture(
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false),
                DrainTimedOut: false);
        }

        try
        {
            closeReaders();
        }
        catch (Exception exception) when (exception is
                   IOException or
                   ObjectDisposedException or
                   InvalidOperationException)
        {
        }

        if (await Task.WhenAny(combined, Task.Delay(drainTimeout)).ConfigureAwait(false) == combined)
        {
            return new ImageGenerationCliOutputCapture(
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false),
                DrainTimedOut: false);
        }

        ObserveLateFault(stdoutTask);
        ObserveLateFault(stderrTask);
        var stdout = CompletedResultOrEmpty(stdoutTask);
        var stderr = AppendDiagnostic(
            CompletedResultOrEmpty(stderrTask),
            $"图片 CLI 输出管道在 {drainTimeout.TotalSeconds:0.###} 秒内未关闭；" +
            "已停止等待，可能有未终止的后代进程仍持有 stdout/stderr 句柄。");
        return new ImageGenerationCliOutputCapture(stdout, stderr, DrainTimedOut: true);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var builder = new StringBuilder(Math.Min(4_096, MaximumCapturedCharacters));
        var buffer = new char[4_096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var remaining = MaximumCapturedCharacters - builder.Length;
                if (remaining > 0)
                {
                    builder.Append(buffer, 0, Math.Min(read, remaining));
                }
            }
        }
        catch (Exception exception) when (exception is
                   IOException or
                   ObjectDisposedException or
                   InvalidOperationException)
        {
            // Closing the readers is the bounded escape hatch when a killed process tree leaves
            // inherited pipe handles open. Preserve whatever diagnostic text was captured first.
        }

        return builder.ToString();
    }

    private static void CloseOutputReaders(Process process)
    {
        TryCloseReader(process.StandardOutput);
        TryCloseReader(process.StandardError);
    }

    private static void TryCloseReader(StreamReader reader)
    {
        try
        {
            reader.Dispose();
        }
        catch (Exception exception) when (exception is
                   IOException or
                   ObjectDisposedException or
                   InvalidOperationException)
        {
        }
    }

    private static string CompletedResultOrEmpty(Task<string> task) =>
        task.Status == TaskStatus.RanToCompletion ? task.Result : string.Empty;

    private static string AppendDiagnostic(string existing, string diagnostic) =>
        string.IsNullOrWhiteSpace(existing) ? diagnostic : $"{existing.TrimEnd()} | {diagnostic}";

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is
                   InvalidOperationException or
                   Win32Exception or
                   NotSupportedException)
        {
        }
    }

    private static async Task WaitAfterKillAsync(Process process)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
                   OperationCanceledException or
                   InvalidOperationException)
        {
        }
    }
}
