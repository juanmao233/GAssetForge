using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GAssetForge.App.Services;

internal enum ImageDeliveryPreflightState
{
    NotChecked,
    LocalReadyNetworkUnverified,
    BuiltInOnly,
    OutputUnavailable
}

internal enum ImageDeliveryNetworkState
{
    NotChecked,
    NotVerified
}

internal sealed record ImageDeliveryExecutableProbeResult(
    bool Success,
    string Detail);

internal interface IImageDeliveryExecutableProbe
{
    Task<ImageDeliveryExecutableProbeResult> ProbeAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

internal interface IImageDeliveryPreflight
{
    Task<ImageDeliveryPreflightResult> CheckAsync(
        IReadOnlyCollection<string> recommendedSourcePaths,
        CancellationToken cancellationToken);
}

internal sealed record ImageDeliveryPreflightResult(
    ImageDeliveryPreflightState State,
    bool CanDispatch,
    bool CliFallbackReady,
    bool CliFallbackLocalPrerequisitesReady,
    ImageDeliveryNetworkState NetworkState,
    bool SandboxNetworkAccessConfigured,
    string? PythonExecutablePath,
    string? ImageGenScriptPath,
    bool ApiKeyAvailable,
    IReadOnlyList<string> Diagnostics,
    string StatusText)
{
    internal static ImageDeliveryPreflightResult NotChecked { get; } = new(
        ImageDeliveryPreflightState.NotChecked,
        CanDispatch: true,
        CliFallbackReady: false,
        CliFallbackLocalPrerequisitesReady: false,
        NetworkState: ImageDeliveryNetworkState.NotChecked,
        SandboxNetworkAccessConfigured: false,
        PythonExecutablePath: null,
        ImageGenScriptPath: null,
        ApiKeyAvailable: false,
        Diagnostics: [],
        StatusText: "尚未执行图片本地交付预检；内置 ImageGen 必须返回明确本地路径后才能提交。");
}

/// <summary>
/// Checks only the deterministic local hand-off seam used after an image model returns. A missing
/// Python/API fallback is diagnostic rather than fatal because the built-in ImageGen path remains
/// valid whenever it exposes a concrete local PNG. An unwritable project output directory is fatal:
/// no provider can complete submit_generation_output without it.
/// </summary>
internal sealed class ImageDeliveryPreflight : IImageDeliveryPreflight
{
    private const long MaximumSourceFileBytes = 544L * 1024L * 1024L;
    private const long MaximumDecodedRgbaBytes = 513L * 1024L * 1024L;
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly string[] PythonFileNames = OperatingSystem.IsWindows()
        ? ["python.exe", "python3.exe", "py.exe"]
        : ["python3", "python", "py"];

    private readonly Func<string?> _pathProvider;
    private readonly Func<string?> _codexHomeProvider;
    private readonly Func<string?> _apiKeyProvider;
    private readonly Func<bool> _sandboxNetworkAccessConfiguredProvider;
    private readonly IImageDeliveryExecutableProbe _executableProbe;

    internal ImageDeliveryPreflight(
        Func<string?>? pathProvider = null,
        Func<string?>? codexHomeProvider = null,
        Func<string?>? apiKeyProvider = null,
        Func<bool>? sandboxNetworkAccessConfiguredProvider = null,
        IImageDeliveryExecutableProbe? executableProbe = null)
    {
        _pathProvider = pathProvider ?? ProcessEnvironmentSnapshot.BuildFreshPathEnvironment;
        _codexHomeProvider = codexHomeProvider ?? ResolveCodexHome;
        _apiKeyProvider = apiKeyProvider ?? (() => Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        _sandboxNetworkAccessConfiguredProvider = sandboxNetworkAccessConfiguredProvider ?? (() => false);
        _executableProbe = executableProbe ?? new SystemImageDeliveryExecutableProbe();
    }

    public async Task<ImageDeliveryPreflightResult> CheckAsync(
        IReadOnlyCollection<string> recommendedSourcePaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recommendedSourcePaths);
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<string>();
        var outputReady = CheckOutputDirectories(recommendedSourcePaths, diagnostics);
        var scriptPath = ResolveImageGenScriptPath();
        var scriptExists = !string.IsNullOrWhiteSpace(scriptPath) && File.Exists(scriptPath);
        if (!scriptExists)
        {
            diagnostics.Add("未找到官方 imagegen skill 的 scripts/image_gen.py");
        }

        var pythonSelection = await FindPythonAsync(
                scriptExists ? scriptPath : null,
                diagnostics,
                cancellationToken)
            .ConfigureAwait(false);
        var python = pythonSelection.CliCompatiblePath ?? pythonSelection.FirstLaunchablePath;
        var scriptReady = scriptExists && pythonSelection.CliCompatiblePath is not null;

        var apiKeyAvailable = !string.IsNullOrWhiteSpace(_apiKeyProvider());
        if (!apiKeyAvailable)
        {
            diagnostics.Add("Agent 子进程环境未配置 OPENAI_API_KEY");
        }

        var cliReady = outputReady && python is not null && scriptReady && apiKeyAvailable;
        var sandboxNetworkAccessConfigured = _sandboxNetworkAccessConfiguredProvider();
        if (!outputReady)
        {
            return new ImageDeliveryPreflightResult(
                ImageDeliveryPreflightState.OutputUnavailable,
                CanDispatch: false,
                CliFallbackReady: false,
                CliFallbackLocalPrerequisitesReady: false,
                NetworkState: ImageDeliveryNetworkState.NotVerified,
                SandboxNetworkAccessConfigured: sandboxNetworkAccessConfigured,
                PythonExecutablePath: python,
                ImageGenScriptPath: scriptExists ? scriptPath : null,
                ApiKeyAvailable: apiKeyAvailable,
                Diagnostics: diagnostics,
                StatusText: "图片本地交付预检失败：输出目录不可写；" + JoinDiagnostics(diagnostics));
        }

        if (!cliReady)
        {
            return new ImageDeliveryPreflightResult(
                ImageDeliveryPreflightState.BuiltInOnly,
                CanDispatch: true,
                CliFallbackReady: false,
                CliFallbackLocalPrerequisitesReady: false,
                NetworkState: ImageDeliveryNetworkState.NotVerified,
                SandboxNetworkAccessConfigured: sandboxNetworkAccessConfigured,
                PythonExecutablePath: python,
                ImageGenScriptPath: scriptExists ? scriptPath : null,
                ApiKeyAvailable: apiKeyAvailable,
                Diagnostics: diagnostics,
                StatusText:
                    "图片本地交付预检降级：项目推荐输出目录可写；内置 ImageGen 返回明确本地路径时仍可继续。" +
                    "CLI 兜底不可用：" + JoinDiagnostics(diagnostics));
        }

        return new ImageDeliveryPreflightResult(
            ImageDeliveryPreflightState.LocalReadyNetworkUnverified,
            CanDispatch: true,
            CliFallbackReady: false,
            CliFallbackLocalPrerequisitesReady: true,
            NetworkState: ImageDeliveryNetworkState.NotVerified,
            SandboxNetworkAccessConfigured: sandboxNetworkAccessConfigured,
            PythonExecutablePath: python,
            ImageGenScriptPath: scriptPath,
            ApiKeyAvailable: true,
            Diagnostics: diagnostics,
            StatusText:
                $"图片本地交付预检：项目推荐输出目录可写；CLI 本地前置已就绪（{python}，官方 image_gen.py 已通过 --help 启动探测，OpenAI SDK 可导入，OPENAI_API_KEY 已配置）。" +
                (sandboxNetworkAccessConfigured
                    ? "受管 Codex workspace-write sandbox 已配置出站网络，但 Image API 连通性、认证、配额与网络仍未验证；不得显示为 Ready。"
                    : "未确认受管 Agent sandbox 已开放出站网络，Image API 网络未验证；不得显示为 Ready。") +
                "内置 ImageGen 返回明确本地路径时仍优先直接提交。" +
                (diagnostics.Count == 0 ? string.Empty : "交付恢复诊断：" + JoinDiagnostics(diagnostics)));
    }

    private async Task<PythonProbeSelection> FindPythonAsync(
        string? scriptPath,
        ICollection<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var candidates = ResolvePythonCandidates(_pathProvider()).ToArray();
        if (candidates.Length == 0)
        {
            diagnostics.Add("PATH 中未找到可执行的 Python/py 候选");
            return new PythonProbeSelection(null, null);
        }

        string? firstLaunchablePath = null;
        var launchFailures = new List<string>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await _executableProbe.ProbeAsync(candidate, ["--version"], cancellationToken)
                .ConfigureAwait(false);
            if (!probe.Success)
            {
                launchFailures.Add($"{candidate}：{SingleLine(probe.Detail)}");
                continue;
            }

            firstLaunchablePath ??= candidate;
            if (scriptPath is null)
            {
                return new PythonProbeSelection(firstLaunchablePath, null);
            }

            var scriptProbe = await _executableProbe.ProbeAsync(
                    candidate,
                    [scriptPath, "--help"],
                    cancellationToken)
                .ConfigureAwait(false);
            if (!scriptProbe.Success)
            {
                diagnostics.Add(
                    $"官方 image_gen.py 无法由 Python 候选启动（{candidate}，{scriptPath}）：" +
                    SingleLine(scriptProbe.Detail));
                continue;
            }

            var sdkProbe = await _executableProbe.ProbeAsync(
                    candidate,
                    ["-c", "from openai import OpenAI; print('openai-sdk-ready')"],
                    cancellationToken)
                .ConfigureAwait(false);
            if (!sdkProbe.Success)
            {
                diagnostics.Add(
                    $"官方 image_gen.py 所需 OpenAI SDK 无法由 Python 候选导入（{candidate}）：" +
                    SingleLine(sdkProbe.Detail));
                continue;
            }

            return new PythonProbeSelection(firstLaunchablePath, candidate);
        }

        if (firstLaunchablePath is null)
        {
            diagnostics.Add(
                "Python/py 候选均无法启动（" + string.Join("；", launchFailures.Take(4)) + "）");
        }
        else if (scriptPath is not null)
        {
            diagnostics.Add("没有 Python 候选同时通过 image_gen.py 与 OpenAI SDK 本地探针");
        }

        return new PythonProbeSelection(firstLaunchablePath, null);
    }

    private string? ResolveImageGenScriptPath()
    {
        var codexHome = _codexHomeProvider();
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(
                codexHome,
                "skills",
                ".system",
                "imagegen",
                "scripts",
                "image_gen.py"));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool CheckOutputDirectories(
        IReadOnlyCollection<string> recommendedSourcePaths,
        ICollection<string> diagnostics)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var rawPaths = recommendedSourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(comparer)
            .ToArray();
        if (rawPaths.Length == 0)
        {
            diagnostics.Add("任务没有项目内绝对 recommendedSourcePath");
            return false;
        }

        var allWritable = true;
        var directories = new HashSet<string>(comparer);
        foreach (var rawPath in rawPaths)
        {
            try
            {
                if (!Path.IsPathFullyQualified(rawPath))
                {
                    throw new InvalidDataException("路径不是绝对路径");
                }

                var fullPath = Path.GetFullPath(rawPath);
                var directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    throw new DirectoryNotFoundException("父目录不存在");
                }

                if (Directory.Exists(fullPath))
                {
                    throw new InvalidDataException("推荐模型源路径当前是目录");
                }

                if (File.Exists(fullPath))
                {
                    if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException("推荐模型源文件不能是符号链接或重解析点");
                    }

                    if (IsUsablePng(fullPath, out var invalidReason))
                    {
                        diagnostics.Add($"精确 recommendedSourcePath 已有有效 PNG，可直接提交恢复：{fullPath}");
                    }
                    else
                    {
                        var quarantinePath = BuildQuarantinePath(fullPath);
                        File.Move(fullPath, quarantinePath);
                        diagnostics.Add(
                            $"精确 recommendedSourcePath 中的损坏文件已安全隔离为 {quarantinePath}：{invalidReason}");
                    }
                }

                directories.Add(Path.TrimEndingDirectorySeparator(directory));
            }
            catch (Exception exception) when (exception is
                       IOException or
                       UnauthorizedAccessException or
                       ArgumentException or
                       NotSupportedException or
                       PathTooLongException or
                       System.Security.SecurityException)
            {
                allWritable = false;
                diagnostics.Add($"推荐输出 {SingleLine(rawPath)} 不可写：{SingleLine(exception.Message)}");
            }
        }

        foreach (var directory in directories)
        {
            var probePath = Path.Combine(directory, $".gassetforge-write-probe-{Guid.NewGuid():N}.tmp");
            try
            {
                using var stream = new FileStream(
                    probePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1,
                    FileOptions.WriteThrough);
                stream.WriteByte(0x47);
                stream.Flush(flushToDisk: true);
            }
            catch (Exception exception) when (exception is
                       IOException or
                       UnauthorizedAccessException or
                       ArgumentException or
                       NotSupportedException or
                       PathTooLongException or
                       System.Security.SecurityException)
            {
                allWritable = false;
                diagnostics.Add($"推荐输出目录 {SingleLine(directory)} 不可写：{SingleLine(exception.Message)}");
            }
            finally
            {
                TryDeleteProbe(probePath);
            }
        }

        return allWritable;
    }

    private static bool IsUsablePng(string path, out string failureReason)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumSourceFileBytes)
            {
                failureReason = $"文件大小必须位于 1 到 {MaximumSourceFileBytes / 1024 / 1024} MiB 之间";
                return false;
            }

            using var stream = info.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> signature = stackalloc byte[PngSignature.Length];
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual(PngSignature))
            {
                failureReason = "PNG 签名无效";
                return false;
            }

            stream.Position = 0;
            var imageInfo = Image.Identify(stream);
            if (imageInfo is null || imageInfo.Width <= 0 || imageInfo.Height <= 0)
            {
                failureReason = "无法识别完整 PNG 画布";
                return false;
            }

            long decodedBytes;
            try
            {
                decodedBytes = checked((long)imageInfo.Width * imageInfo.Height * 4L);
            }
            catch (OverflowException)
            {
                failureReason = "PNG 解码尺寸溢出";
                return false;
            }

            if (decodedBytes > MaximumDecodedRgbaBytes)
            {
                failureReason =
                    $"PNG 解码数据不能超过 {MaximumDecodedRgbaBytes / 1024 / 1024} MiB";
                return false;
            }

            stream.Position = 0;
            using var image = Image.Load<Rgba32>(stream);
            if (image.Width != imageInfo.Width || image.Height != imageInfo.Height)
            {
                failureReason = "PNG 识别尺寸与完整解码尺寸不一致";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is
                   IOException or
                   UnauthorizedAccessException or
                   InvalidImageContentException or
                   UnknownImageFormatException or
                   NotSupportedException)
        {
            failureReason = SingleLine(exception.Message);
            return false;
        }
    }

    private sealed record PythonProbeSelection(
        string? FirstLaunchablePath,
        string? CliCompatiblePath);

    private static string BuildQuarantinePath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidDataException("损坏模型源文件缺少父目录。");
        var fileName = Path.GetFileName(sourcePath);
        return Path.Combine(
            directory,
            $"{fileName}.invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.quarantine");
    }

    private static IEnumerable<string> ResolvePythonCandidates(string? pathEnvironment)
    {
        if (string.IsNullOrWhiteSpace(pathEnvironment))
        {
            yield break;
        }

        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (var rawDirectory in pathEnvironment.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var fileName in PythonFileNames)
            {
                string candidate;
                try
                {
                    candidate = Path.GetFullPath(Path.Combine(directory, fileName));
                }
                catch (Exception exception) when (exception is
                           ArgumentException or
                           NotSupportedException or
                           PathTooLongException)
                {
                    continue;
                }

                if (seen.Add(candidate) && File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static string? ResolveCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, ".codex");
    }

    private static string JoinDiagnostics(IEnumerable<string> diagnostics)
    {
        var normalized = diagnostics
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(SingleLine)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return normalized.Length == 0 ? "未提供更多诊断" : string.Join("；", normalized);
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static void TryDeleteProbe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is
                   IOException or
                   UnauthorizedAccessException or
                   System.Security.SecurityException)
        {
        }
    }

    private sealed class SystemImageDeliveryExecutableProbe : IImageDeliveryExecutableProbe
    {
        public async Task<ImageDeliveryExecutableProbeResult> ProbeAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                {
                    return new ImageDeliveryExecutableProbeResult(false, "操作系统未启动该候选");
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    return new ImageDeliveryExecutableProbeResult(false, "启动探测 5 秒超时");
                }

                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                var detail = SingleLine(string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
                return process.ExitCode == 0
                    ? new ImageDeliveryExecutableProbeResult(true, detail)
                    : new ImageDeliveryExecutableProbeResult(
                        false,
                        $"退出码 {process.ExitCode}{(detail.Length == 0 ? string.Empty : $"：{detail}")}");
            }
            catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
            {
                return new ImageDeliveryExecutableProbeResult(false, SingleLine(exception.Message));
            }
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
            catch
            {
            }
        }
    }
}
