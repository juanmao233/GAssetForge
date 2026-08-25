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
    IReadOnlyList<string> PythonLauncherArguments,
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
        PythonLauncherArguments: [],
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
    private static readonly string[] PythonExecutableFileNames = OperatingSystem.IsWindows()
        ? ["python.exe", "python3.exe"]
        : ["python3", "python"];
    private static readonly string[] PythonLauncherFileNames = OperatingSystem.IsWindows()
        ? ["py.exe"]
        : [];

    private readonly Func<string?> _pathProvider;
    private readonly Func<string?> _configuredPythonPathProvider;
    private readonly Func<string?> _localAppDataProvider;
    private readonly Func<string?> _codexHomeProvider;
    private readonly Func<string?> _apiKeyProvider;
    private readonly Func<bool> _sandboxNetworkAccessConfiguredProvider;
    private readonly IImageDeliveryExecutableProbe _executableProbe;

    internal ImageDeliveryPreflight(
        Func<string?>? pathProvider = null,
        Func<string?>? configuredPythonPathProvider = null,
        Func<string?>? localAppDataProvider = null,
        Func<string?>? codexHomeProvider = null,
        Func<string?>? apiKeyProvider = null,
        Func<bool>? sandboxNetworkAccessConfiguredProvider = null,
        IImageDeliveryExecutableProbe? executableProbe = null,
        Func<string, string?>? environmentVariableProvider = null)
    {
        var readEnvironmentVariable = environmentVariableProvider ??
                                      ProcessEnvironmentSnapshot.ReadFreshEnvironmentVariable;
        _pathProvider = pathProvider ?? ProcessEnvironmentSnapshot.BuildFreshPathEnvironment;
        _configuredPythonPathProvider = configuredPythonPathProvider ??
                                        (() => readEnvironmentVariable(
                                            "GASSETFORGE_PYTHON_EXECUTABLE"));
        _localAppDataProvider = localAppDataProvider ?? ResolveLocalApplicationData;
        _codexHomeProvider = codexHomeProvider ?? (() => ResolveCodexHome(readEnvironmentVariable));
        _apiKeyProvider = apiKeyProvider ?? (() => readEnvironmentVariable("OPENAI_API_KEY"));
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

        var apiKey = _apiKeyProvider();
        var sensitiveValues = string.IsNullOrWhiteSpace(apiKey)
            ? Array.Empty<string>()
            : new[] { apiKey };
        var pythonSelection = await FindPythonAsync(
                scriptExists ? scriptPath : null,
                diagnostics,
                sensitiveValues,
                cancellationToken)
            .ConfigureAwait(false);
        var pythonCommand = pythonSelection.CliCompatibleCommand ?? pythonSelection.FirstLaunchableCommand;
        var python = pythonCommand?.ExecutablePath;
        var pythonLauncherArguments = pythonCommand?.PrefixArguments ?? [];
        var scriptReady = scriptExists && pythonSelection.CliCompatibleCommand is not null;

        var apiKeyAvailable = !string.IsNullOrWhiteSpace(apiKey);
        if (!apiKeyAvailable)
        {
            diagnostics.Add("Agent 子进程环境未配置 OPENAI_API_KEY");
        }

        var cliReady = outputReady && python is not null && scriptReady && apiKeyAvailable;
        var sandboxNetworkAccessConfigured = _sandboxNetworkAccessConfiguredProvider();
        var safeDiagnostics = NormalizeDiagnostics(diagnostics, sensitiveValues);
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
                PythonLauncherArguments: pythonLauncherArguments,
                ImageGenScriptPath: scriptExists ? scriptPath : null,
                ApiKeyAvailable: apiKeyAvailable,
                Diagnostics: safeDiagnostics,
                StatusText: "图片本地交付预检失败：输出目录不可写；" +
                            JoinDiagnostics(safeDiagnostics));
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
                PythonLauncherArguments: pythonLauncherArguments,
                ImageGenScriptPath: scriptExists ? scriptPath : null,
                ApiKeyAvailable: apiKeyAvailable,
                Diagnostics: safeDiagnostics,
                StatusText:
                    "图片本地交付预检降级：项目推荐输出目录可写；内置 ImageGen 返回明确本地路径时仍可继续。" +
                    "CLI 兜底不可用：" + JoinDiagnostics(safeDiagnostics));
        }

        return new ImageDeliveryPreflightResult(
            ImageDeliveryPreflightState.LocalReadyNetworkUnverified,
            CanDispatch: true,
            CliFallbackReady: false,
            CliFallbackLocalPrerequisitesReady: true,
            NetworkState: ImageDeliveryNetworkState.NotVerified,
            SandboxNetworkAccessConfigured: sandboxNetworkAccessConfigured,
            PythonExecutablePath: python,
            PythonLauncherArguments: pythonLauncherArguments,
            ImageGenScriptPath: scriptPath,
            ApiKeyAvailable: true,
            Diagnostics: safeDiagnostics,
            StatusText:
                $"图片本地交付预检：项目推荐输出目录可写；CLI 本地前置已就绪（{FormatPythonCommand(pythonCommand!)}，官方 image_gen.py 已通过 --help 启动探测，OpenAI SDK 可导入，OPENAI_API_KEY 已配置）。" +
                (sandboxNetworkAccessConfigured
                    ? "受管 Codex workspace-write sandbox 已配置出站网络，但 Image API 连通性、认证、配额与网络仍未验证；不得显示为 Ready。"
                    : "未确认受管 Agent sandbox 已开放出站网络，Image API 网络未验证；不得显示为 Ready。") +
                "内置 ImageGen 返回明确本地路径时仍优先直接提交。" +
                (safeDiagnostics.Count == 0
                    ? string.Empty
                    : "交付恢复诊断：" + JoinDiagnostics(safeDiagnostics)));
    }

    private async Task<PythonProbeSelection> FindPythonAsync(
        string? scriptPath,
        ICollection<string> diagnostics,
        IReadOnlyCollection<string> sensitiveValues,
        CancellationToken cancellationToken)
    {
        var candidates = ResolvePythonCandidates(
                _configuredPythonPathProvider(),
                _localAppDataProvider(),
                _pathProvider(),
                diagnostics)
            .ToArray();
        if (candidates.Length == 0)
        {
            diagnostics.Add("手动配置、LocalAppData 常见安装目录与最新 PATH 中均未找到 Python/py 候选");
            return new PythonProbeSelection(null, null);
        }

        PythonLaunchCommand? firstLaunchableCommand = null;
        var launchFailures = new List<string>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await _executableProbe.ProbeAsync(
                    candidate.ExecutablePath,
                    BuildPythonArguments(candidate, "--version"),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!probe.Success)
            {
                launchFailures.Add(
                    $"{FormatPythonCommand(candidate)}（{candidate.Source}）：" +
                    RedactSensitiveValues(SingleLine(probe.Detail), sensitiveValues));
                continue;
            }

            firstLaunchableCommand ??= candidate;
            if (scriptPath is null)
            {
                return new PythonProbeSelection(firstLaunchableCommand, null);
            }

            var scriptProbe = await _executableProbe.ProbeAsync(
                    candidate.ExecutablePath,
                    BuildPythonArguments(candidate, scriptPath, "--help"),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!scriptProbe.Success)
            {
                diagnostics.Add(
                    $"官方 image_gen.py 无法由 Python 候选启动（{FormatPythonCommand(candidate)}，{scriptPath}）：" +
                    RedactSensitiveValues(SingleLine(scriptProbe.Detail), sensitiveValues));
                continue;
            }

            var sdkProbe = await _executableProbe.ProbeAsync(
                    candidate.ExecutablePath,
                    BuildPythonArguments(
                        candidate,
                        "-c",
                        "from openai import OpenAI; print('openai-sdk-ready')"),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!sdkProbe.Success)
            {
                diagnostics.Add(
                    $"官方 image_gen.py 所需 OpenAI SDK 无法由 Python 候选导入（{FormatPythonCommand(candidate)}）：" +
                    RedactSensitiveValues(SingleLine(sdkProbe.Detail), sensitiveValues));
                continue;
            }

            return new PythonProbeSelection(firstLaunchableCommand, candidate);
        }

        if (firstLaunchableCommand is null)
        {
            diagnostics.Add(
                "Python/py 候选均无法启动（" + string.Join("；", launchFailures.Take(4)) + "）");
        }
        else if (scriptPath is not null)
        {
            diagnostics.Add("没有 Python 候选同时通过 image_gen.py 与 OpenAI SDK 本地探针");
        }

        return new PythonProbeSelection(firstLaunchableCommand, null);
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

    private sealed record PythonLaunchCommand(
        string ExecutablePath,
        IReadOnlyList<string> PrefixArguments,
        string Source);

    private sealed record PythonProbeSelection(
        PythonLaunchCommand? FirstLaunchableCommand,
        PythonLaunchCommand? CliCompatibleCommand);

    private static string BuildQuarantinePath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidDataException("损坏模型源文件缺少父目录。");
        var fileName = Path.GetFileName(sourcePath);
        return Path.Combine(
            directory,
            $"{fileName}.invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.quarantine");
    }

    private static IEnumerable<PythonLaunchCommand> ResolvePythonCandidates(
        string? configuredPythonPath,
        string? localAppData,
        string? pathEnvironment,
        ICollection<string> diagnostics)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);

        if (!string.IsNullOrWhiteSpace(configuredPythonPath))
        {
            var configured = TryCreatePythonCommand(
                configuredPythonPath,
                "手动配置 GASSETFORGE_PYTHON_EXECUTABLE");
            if (configured is not null && File.Exists(configured.ExecutablePath))
            {
                if (seen.Add(BuildPythonCommandKey(configured)))
                {
                    yield return configured;
                }
            }
            else
            {
                diagnostics.Add(
                    $"手动配置的 Python 路径不存在或无效：{SingleLine(configuredPythonPath)}");
            }
        }

        foreach (var command in ResolveLocalAppDataPythonCommands(localAppData))
        {
            if (seen.Add(BuildPythonCommandKey(command)))
            {
                yield return command;
            }
        }

        var pathDirectories = SplitPathDirectories(pathEnvironment).ToArray();
        foreach (var directory in pathDirectories)
        {
            foreach (var fileName in PythonExecutableFileNames)
            {
                var command = TryCreatePythonCommand(
                    Path.Combine(directory, fileName),
                    "最新 PATH");
                if (command is not null &&
                    File.Exists(command.ExecutablePath) &&
                    seen.Add(BuildPythonCommandKey(command)))
                {
                    yield return command;
                }
            }
        }

        foreach (var directory in pathDirectories)
        {
            foreach (var fileName in PythonLauncherFileNames)
            {
                var command = TryCreatePythonCommand(
                    Path.Combine(directory, fileName),
                    "最新 PATH 中的 Python Launcher");
                if (command is not null &&
                    File.Exists(command.ExecutablePath) &&
                    seen.Add(BuildPythonCommandKey(command)))
                {
                    yield return command;
                }
            }
        }
    }

    private static IEnumerable<PythonLaunchCommand> ResolveLocalAppDataPythonCommands(
        string? localAppData)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(localAppData))
        {
            yield break;
        }

        string pythonRoot;
        try
        {
            pythonRoot = Path.GetFullPath(Path.Combine(
                localAppData.Trim().Trim('"'),
                "Programs",
                "Python"));
        }
        catch (Exception exception) when (exception is
                   ArgumentException or
                   NotSupportedException or
                   PathTooLongException)
        {
            yield break;
        }

        DirectoryInfo[] installDirectories;
        try
        {
            installDirectories = Directory.Exists(pythonRoot)
                ? new DirectoryInfo(pythonRoot)
                    .EnumerateDirectories("Python*", SearchOption.TopDirectoryOnly)
                    .Where(directory =>
                        (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                    .OrderByDescending(directory => ParsePythonDirectoryVersion(directory.Name))
                    .ThenByDescending(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
        }
        catch (Exception exception) when (exception is
                   IOException or
                   UnauthorizedAccessException or
                   System.Security.SecurityException)
        {
            installDirectories = [];
        }

        foreach (var installDirectory in installDirectories)
        {
            foreach (var fileName in PythonExecutableFileNames)
            {
                var command = TryCreatePythonCommand(
                    Path.Combine(installDirectory.FullName, fileName),
                    "LocalAppData Python 常见安装目录");
                if (command is not null && File.Exists(command.ExecutablePath))
                {
                    yield return command;
                }
            }
        }

        foreach (var fileName in PythonLauncherFileNames)
        {
            var launcher = TryCreatePythonCommand(
                Path.Combine(pythonRoot, "Launcher", fileName),
                "LocalAppData Python Launcher");
            if (launcher is not null && File.Exists(launcher.ExecutablePath))
            {
                yield return launcher;
            }
        }
    }

    private static IEnumerable<string> SplitPathDirectories(string? pathEnvironment)
    {
        if (string.IsNullOrWhiteSpace(pathEnvironment))
        {
            yield break;
        }

        foreach (var rawDirectory in pathEnvironment.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(directory);
            }
            catch (Exception exception) when (exception is
                       ArgumentException or
                       NotSupportedException or
                       PathTooLongException)
            {
                continue;
            }

            yield return normalized;
        }
    }

    private static PythonLaunchCommand? TryCreatePythonCommand(string rawPath, string source)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
            var fullPath = Path.GetFullPath(expanded);
            var fileName = Path.GetFileName(fullPath);
            var prefixArguments = PythonLauncherFileNames.Contains(
                fileName,
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
                ? new[] { "-3" }
                : [];
            return new PythonLaunchCommand(fullPath, prefixArguments, source);
        }
        catch (Exception exception) when (exception is
                   ArgumentException or
                   NotSupportedException or
                   PathTooLongException)
        {
            return null;
        }
    }

    private static int ParsePythonDirectoryVersion(string directoryName)
    {
        var digits = new string(directoryName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var version) ? version : -1;
    }

    private static string BuildPythonCommandKey(PythonLaunchCommand command) =>
        command.ExecutablePath + "\0" + string.Join("\0", command.PrefixArguments);

    private static IReadOnlyList<string> BuildPythonArguments(
        PythonLaunchCommand command,
        params string[] arguments) =>
        command.PrefixArguments.Concat(arguments).ToArray();

    private static string FormatPythonCommand(PythonLaunchCommand command) =>
        command.PrefixArguments.Count == 0
            ? command.ExecutablePath
            : command.ExecutablePath + " " + string.Join(' ', command.PrefixArguments);

    private static string? ResolveCodexHome(Func<string, string?> environmentVariableProvider)
    {
        var configured = environmentVariableProvider("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, ".codex");
    }

    private static string? ResolveLocalApplicationData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData) ? null : localAppData;
    }

    private static IReadOnlyList<string> NormalizeDiagnostics(
        IEnumerable<string> diagnostics,
        IReadOnlyCollection<string> sensitiveValues)
        => diagnostics
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => RedactSensitiveValues(SingleLine(message), sensitiveValues))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string JoinDiagnostics(IReadOnlyCollection<string> diagnostics) =>
        diagnostics.Count == 0 ? "未提供更多诊断" : string.Join("；", diagnostics);

    private static string RedactSensitiveValues(
        string value,
        IReadOnlyCollection<string> sensitiveValues)
    {
        var redacted = value;
        foreach (var sensitiveValue in sensitiveValues)
        {
            if (!string.IsNullOrEmpty(sensitiveValue))
            {
                redacted = redacted.Replace(
                    sensitiveValue,
                    "[REDACTED]",
                    StringComparison.Ordinal);
            }
        }

        return redacted;
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
