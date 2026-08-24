using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GAssetForge.App.Services.AgentRuntime;

internal sealed record CodexExecutableLaunch(
    string ProcessFileName,
    IReadOnlyList<string> PrefixArguments,
    string CodexExecutablePath,
    bool HasCodeModeCompanion);

internal enum CodexExecutableCandidateStatus
{
    Accepted,
    Rejected,
    Skipped
}

internal sealed record CodexExecutableCandidateDiagnostic(
    string Source,
    string CandidatePath,
    CodexExecutableCandidateStatus Status,
    string Reason);

internal readonly record struct CodexExecutableProbeResult(bool IsSuccess, string Reason)
{
    internal static CodexExecutableProbeResult Successful(string reason) =>
        new(true, NormalizeReason(reason, "--version 探测成功。"));

    internal static CodexExecutableProbeResult Failed(string reason) =>
        new(false, NormalizeReason(reason, "--version 探测失败。"));

    private static string NormalizeReason(string reason, string fallback) =>
        string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();
}

internal sealed record CodexExecutableResolution(
    CodexExecutableLaunch? Launch,
    IReadOnlyList<CodexExecutableCandidateDiagnostic> Candidates,
    string Summary)
{
    internal bool IsSuccess => Launch is not null;
}

internal sealed class CodexExecutableResolutionException : InvalidOperationException
{
    internal CodexExecutableResolutionException(
        string message,
        IReadOnlyList<CodexExecutableCandidateDiagnostic> candidates)
        : base(message)
    {
        Candidates = candidates.ToArray();
    }

    internal IReadOnlyList<CodexExecutableCandidateDiagnostic> Candidates { get; }
}

internal static class CodexExecutableResolver
{
    internal const string CodeModeHostFileName = "codex-code-mode-host.exe";
    private const string AutomaticExecutableName = "codex";
    private const string AppDataNpmSource = "APPDATA npm shim";
    private const string LocalAppDataOfficialInstallSource = "LOCALAPPDATA 官方安装";
    private const string PathShimSource = "PATH shim";
    private static readonly TimeSpan LaunchProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<string, CodexExecutableProbeResult>
        SuccessfulLaunchProbeCache = new(StringComparer.OrdinalIgnoreCase);

    internal static CodexExecutableLaunch Resolve(
        string requestedExecutable,
        string? applicationDirectory = null,
        string? pathEnvironment = null,
        Func<CodexExecutableLaunch, bool>? launchProbe = null,
        string? applicationDataDirectory = null,
        string? localApplicationDataDirectory = null)
    {
        Func<CodexExecutableLaunch, CodexExecutableProbeResult>? detailedProbe = launchProbe is null
            ? null
            : candidate => launchProbe(candidate)
                ? CodexExecutableProbeResult.Successful("启动探测成功。")
                : CodexExecutableProbeResult.Failed("启动探测失败；测试探针未提供详细原因。");
        var resolution = ResolveWithDiagnostics(
            requestedExecutable,
            applicationDirectory,
            pathEnvironment,
            detailedProbe,
            applicationDataDirectory,
            localApplicationDataDirectory);
        return resolution.Launch ?? throw new CodexExecutableResolutionException(
            resolution.Summary,
            resolution.Candidates);
    }

    internal static CodexExecutableResolution ResolveWithDiagnostics(
        string requestedExecutable,
        string? applicationDirectory = null,
        string? pathEnvironment = null,
        Func<CodexExecutableLaunch, CodexExecutableProbeResult>? launchProbe = null,
        string? applicationDataDirectory = null,
        string? localApplicationDataDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedExecutable);
        var requested = requestedExecutable.Trim().Trim('"');
        var effectivePathEnvironment = pathEnvironment ??
                                       Environment.GetEnvironmentVariable("PATH");
        var canLaunch = launchProbe ??
                        (candidate => ProbeCandidateWithSuccessfulCache(
                            candidate,
                            effectivePathEnvironment));
        if (!string.Equals(requested, AutomaticExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return ResolveExplicitExecutable(requested, canLaunch);
        }

        var applicationPath = NormalizeDirectory(applicationDirectory ?? AppContext.BaseDirectory);
        var appDataPath = ResolveApplicationDataDirectory(applicationDataDirectory);
        var localAppDataPath = ResolveLocalApplicationDataDirectory(localApplicationDataDirectory);
        var pathDirectories = EnumeratePathDirectories(effectivePathEnvironment)
            .Where(path => !PathEquals(path, applicationPath))
            .ToArray();
        var diagnostics = new List<CodexExecutableCandidateDiagnostic>();
        var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(appDataPath))
        {
            var npmDirectory = Path.Combine(appDataPath, "npm");
            foreach (var npmShim in EnumeratePathShims(npmDirectory))
            {
                var accepted = EvaluateCandidate(
                    npmShim,
                    AppDataNpmSource,
                    canLaunch,
                    diagnostics,
                    seenCandidates);
                if (accepted is not null)
                {
                    return CreateSuccessfulResolution(accepted, diagnostics);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(localAppDataPath))
        {
            var officialInstallDirectory = Path.Combine(
                localAppDataPath,
                "Programs",
                "OpenAI",
                "Codex",
                "bin");
            var officialInstall = TryCreateOfficialInstall(officialInstallDirectory);
            if (officialInstall is not null)
            {
                var accepted = EvaluateCandidate(
                    officialInstall,
                    LocalAppDataOfficialInstallSource,
                    canLaunch,
                    diagnostics,
                    seenCandidates);
                if (accepted is not null)
                {
                    return CreateSuccessfulResolution(accepted, diagnostics);
                }
            }
        }

        foreach (var directory in pathDirectories)
        {
            foreach (var pathShim in EnumeratePathShims(directory))
            {
                var accepted = EvaluateCandidate(
                    pathShim,
                    PathShimSource,
                    canLaunch,
                    diagnostics,
                    seenCandidates);
                if (accepted is not null)
                {
                    return CreateSuccessfulResolution(accepted, diagnostics);
                }
            }
        }

        foreach (var directory in pathDirectories)
        {
            var completeRuntime = TryCreateCompleteRuntime(directory);
            if (completeRuntime is null)
            {
                continue;
            }

            var accepted = EvaluateCandidate(
                completeRuntime,
                "PATH 完整 Runtime",
                canLaunch,
                diagnostics,
                seenCandidates);
            if (accepted is not null)
            {
                return CreateSuccessfulResolution(accepted, diagnostics);
            }
        }

        var completeApplicationRuntime = TryCreateCompleteRuntime(applicationPath);
        if (completeApplicationRuntime is not null)
        {
            var accepted = EvaluateCandidate(
                completeApplicationRuntime,
                "应用目录完整 Runtime",
                canLaunch,
                diagnostics,
                seenCandidates);
            if (accepted is not null)
            {
                return CreateSuccessfulResolution(accepted, diagnostics);
            }
        }

        foreach (var directory in pathDirectories)
        {
            var legacyExecutable = TryCreateLegacyExecutable(directory);
            if (legacyExecutable is null)
            {
                continue;
            }

            var accepted = EvaluateCandidate(
                legacyExecutable,
                "PATH codex.exe",
                canLaunch,
                diagnostics,
                seenCandidates);
            if (accepted is not null)
            {
                return CreateSuccessfulResolution(accepted, diagnostics);
            }
        }

        AddIncompleteApplicationRuntimeDiagnostic(
            applicationPath,
            diagnostics,
            seenCandidates);
        return CreateFailedResolution(diagnostics);
    }

    internal static void ClearLaunchProbeCache() => SuccessfulLaunchProbeCache.Clear();

    private static CodexExecutableResolution ResolveExplicitExecutable(
        string requestedExecutable,
        Func<CodexExecutableLaunch, CodexExecutableProbeResult> launchProbe)
    {
        var diagnostics = new List<CodexExecutableCandidateDiagnostic>();
        var extension = Path.GetExtension(requestedExecutable);
        if (!IsProbeableExplicitExtension(extension))
        {
            var unprobedLaunch = new CodexExecutableLaunch(
                requestedExecutable,
                [],
                requestedExecutable,
                HasCodeModeCompanion: false);
            diagnostics.Add(new CodexExecutableCandidateDiagnostic(
                "用户配置命令",
                requestedExecutable,
                CodexExecutableCandidateStatus.Accepted,
                "非 .cmd、.bat 或 .exe 自定义命令，保留兼容模式并在正式启动时解析。"));
            return CreateSuccessfulResolution(unprobedLaunch, diagnostics);
        }

        string executablePath;
        try
        {
            executablePath = Path.GetFullPath(requestedExecutable);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            diagnostics.Add(new CodexExecutableCandidateDiagnostic(
                "用户配置路径",
                requestedExecutable,
                CodexExecutableCandidateStatus.Rejected,
                $"路径无效：{exception.Message}"));
            return CreateFailedResolution(diagnostics);
        }

        if (!File.Exists(executablePath))
        {
            diagnostics.Add(new CodexExecutableCandidateDiagnostic(
                "用户配置路径",
                executablePath,
                CodexExecutableCandidateStatus.Rejected,
                "文件不存在。"));
            return CreateFailedResolution(diagnostics);
        }

        var candidate = IsCommandScriptExtension(extension)
            ? CreateScriptLaunch(executablePath)
            : new CodexExecutableLaunch(
                executablePath,
                [],
                executablePath,
                HasCodeModeCompanion: File.Exists(
                    Path.Combine(
                        Path.GetDirectoryName(executablePath) ?? string.Empty,
                        CodeModeHostFileName)));
        var accepted = EvaluateCandidate(
            candidate,
            "用户配置路径",
            launchProbe,
            diagnostics,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return accepted is null
            ? CreateFailedResolution(diagnostics)
            : CreateSuccessfulResolution(accepted, diagnostics);
    }

    private static CodexExecutableLaunch? EvaluateCandidate(
        CodexExecutableLaunch candidate,
        string source,
        Func<CodexExecutableLaunch, CodexExecutableProbeResult> launchProbe,
        ICollection<CodexExecutableCandidateDiagnostic> diagnostics,
        ISet<string> seenCandidates)
    {
        if (!seenCandidates.Add(candidate.CodexExecutablePath))
        {
            return null;
        }

        if (IsWindowsAppsExecutable(candidate.CodexExecutablePath))
        {
            diagnostics.Add(new CodexExecutableCandidateDiagnostic(
                source,
                candidate.CodexExecutablePath,
                CodexExecutableCandidateStatus.Skipped,
                "已跳过 WindowsApps 包内 codex.exe：该文件可被发现，但普通桌面进程直接启动通常会返回“拒绝访问”；请使用 APPDATA npm shim 或手动指定可执行的 Codex CLI。"));
            return null;
        }

        CodexExecutableProbeResult probeResult;
        try
        {
            probeResult = launchProbe(candidate);
        }
        catch (Exception exception)
        {
            probeResult = CodexExecutableProbeResult.Failed(
                $"启动探针抛出 {exception.GetType().Name}：{exception.Message}");
        }

        diagnostics.Add(new CodexExecutableCandidateDiagnostic(
            source,
            candidate.CodexExecutablePath,
            probeResult.IsSuccess
                ? CodexExecutableCandidateStatus.Accepted
                : CodexExecutableCandidateStatus.Rejected,
            probeResult.Reason));
        return probeResult.IsSuccess ? candidate : null;
    }

    private static IEnumerable<CodexExecutableLaunch> EnumeratePathShims(string directory)
    {
        foreach (var scriptName in new[] { "codex.cmd", "codex.bat" })
        {
            string scriptPath;
            try
            {
                scriptPath = Path.Combine(directory, scriptName);
            }
            catch (Exception exception) when (exception is
                ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                yield break;
            }

            if (File.Exists(scriptPath))
            {
                yield return CreateScriptLaunch(Path.GetFullPath(scriptPath));
            }
        }
    }

    private static CodexExecutableLaunch CreateScriptLaunch(string scriptPath) =>
        new(
            GetCommandProcessorPath(),
            ["/d", "/s", "/c", "call", Path.GetFullPath(scriptPath)],
            Path.GetFullPath(scriptPath),
            HasCodeModeCompanion: false);

    private static string GetCommandProcessorPath()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        if (!string.IsNullOrWhiteSpace(commandProcessor))
        {
            return commandProcessor;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
    }

    private static CodexExecutableLaunch? TryCreateCompleteRuntime(string directory)
    {
        var executablePath = Path.Combine(directory, "codex.exe");
        var companionPath = Path.Combine(directory, CodeModeHostFileName);
        return File.Exists(executablePath) && File.Exists(companionPath)
            ? new CodexExecutableLaunch(
                Path.GetFullPath(executablePath),
                [],
                Path.GetFullPath(executablePath),
                HasCodeModeCompanion: true)
            : null;
    }

    private static CodexExecutableLaunch? TryCreateOfficialInstall(string directory)
    {
        var executablePath = Path.Combine(directory, "codex.exe");
        if (!File.Exists(executablePath))
        {
            return null;
        }

        return new CodexExecutableLaunch(
            Path.GetFullPath(executablePath),
            [],
            Path.GetFullPath(executablePath),
            HasCodeModeCompanion: File.Exists(
                Path.Combine(directory, CodeModeHostFileName)));
    }

    private static CodexExecutableLaunch? TryCreateLegacyExecutable(string directory)
    {
        var executablePath = Path.Combine(directory, "codex.exe");
        return File.Exists(executablePath)
            ? new CodexExecutableLaunch(
                Path.GetFullPath(executablePath),
                [],
                Path.GetFullPath(executablePath),
                HasCodeModeCompanion: false)
            : null;
    }

    private static CodexExecutableProbeResult ProbeCandidateWithSuccessfulCache(
        CodexExecutableLaunch candidate,
        string? pathEnvironment)
    {
        var cacheKey = CreateProbeCacheKey(candidate, pathEnvironment);
        if (SuccessfulLaunchProbeCache.TryGetValue(cacheKey, out var cachedResult))
        {
            return cachedResult with
            {
                Reason = $"{cachedResult.Reason.TrimEnd()}（成功结果缓存）"
            };
        }

        var probeResult = ProbeCandidate(candidate, pathEnvironment);
        if (probeResult.IsSuccess)
        {
            SuccessfulLaunchProbeCache.TryAdd(cacheKey, probeResult);
        }

        return probeResult;
    }

    private static string CreateProbeCacheKey(
        CodexExecutableLaunch candidate,
        string? pathEnvironment) =>
        string.Join(
            '\0',
            candidate.ProcessFileName,
            candidate.CodexExecutablePath,
            string.Join('\0', candidate.PrefixArguments),
            pathEnvironment ?? string.Empty);

    private static CodexExecutableProbeResult ProbeCandidate(
        CodexExecutableLaunch candidate,
        string? pathEnvironment)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = candidate.ProcessFileName,
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            CodexProcessLaunch.ConfigureUtf8Redirection(startInfo);
            if (pathEnvironment is not null)
            {
                startInfo.Environment["PATH"] = pathEnvironment;
            }

            foreach (var prefixArgument in candidate.PrefixArguments)
            {
                startInfo.ArgumentList.Add(prefixArgument);
            }

            startInfo.ArgumentList.Add("--version");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return CodexExecutableProbeResult.Failed(
                    "Process.Start 未返回进程实例。");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)LaunchProbeTimeout.TotalMilliseconds))
            {
                TryKill(process);
                return CodexExecutableProbeResult.Failed(
                    $"执行 --version 超过 {LaunchProbeTimeout.TotalSeconds:0} 秒，已终止探测进程。");
            }

            var output = standardOutput.GetAwaiter().GetResult();
            var error = standardError.GetAwaiter().GetResult();
            var detail = FormatProbeOutput(output, error);
            if (process.ExitCode != 0)
            {
                return CodexExecutableProbeResult.Failed(
                    string.IsNullOrWhiteSpace(detail)
                        ? $"--version 退出码 {process.ExitCode}。"
                        : $"--version 退出码 {process.ExitCode}：{detail}");
            }

            return CodexExecutableProbeResult.Successful(
                string.IsNullOrWhiteSpace(detail)
                    ? "--version 探测成功。"
                    : $"--version 探测成功：{detail}");
        }
        catch (Exception exception) when (exception is
            Win32Exception or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            return CodexExecutableProbeResult.Failed(
                $"{exception.GetType().Name}：{exception.Message}");
        }
    }

    private static string FormatProbeOutput(string standardOutput, string standardError)
    {
        var combined = string.Join(
            " | ",
            new[] { standardOutput, standardError }
                .SelectMany(value => value.Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Take(3));
        const int maximumLength = 500;
        return combined.Length <= maximumLength
            ? combined
            : string.Concat(combined.AsSpan(0, maximumLength), "…");
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

    private static void AddIncompleteApplicationRuntimeDiagnostic(
        string applicationDirectory,
        ICollection<CodexExecutableCandidateDiagnostic> diagnostics,
        ISet<string> seenCandidates)
    {
        var executablePath = Path.Combine(applicationDirectory, "codex.exe");
        var companionPath = Path.Combine(applicationDirectory, CodeModeHostFileName);
        if (!File.Exists(executablePath) || File.Exists(companionPath))
        {
            return;
        }

        executablePath = Path.GetFullPath(executablePath);
        if (!seenCandidates.Add(executablePath))
        {
            return;
        }

        diagnostics.Add(new CodexExecutableCandidateDiagnostic(
            "应用目录本地副本",
            executablePath,
            CodexExecutableCandidateStatus.Rejected,
            $"检测到不完整的 Codex Runtime；缺少同目录组件：{Path.GetFullPath(companionPath)}。该本地副本会遮蔽完整安装，请移除它或发布完整 Runtime。"));
    }

    private static CodexExecutableResolution CreateSuccessfulResolution(
        CodexExecutableLaunch launch,
        IEnumerable<CodexExecutableCandidateDiagnostic> diagnostics)
    {
        var candidateArray = diagnostics.ToArray();
        return new CodexExecutableResolution(
            launch,
            candidateArray,
            $"已找到可用的 Codex Runtime：{launch.CodexExecutablePath}");
    }

    private static CodexExecutableResolution CreateFailedResolution(
        IEnumerable<CodexExecutableCandidateDiagnostic> diagnostics)
    {
        var candidateArray = diagnostics.ToArray();
        var message = new StringBuilder(
            "未找到可用的 Codex Runtime。请安装 Codex CLI、确认 %APPDATA%\\npm\\codex.cmd 或 %LOCALAPPDATA%\\Programs\\OpenAI\\Codex\\bin\\codex.exe 可执行，或手动指定 Codex CLI 路径。");
        if (candidateArray.Length == 0)
        {
            message.AppendLine()
                .Append("没有发现现存候选文件；已检查 APPDATA npm、LOCALAPPDATA 官方安装、PATH 和应用目录。");
        }
        else
        {
            message.AppendLine().Append("候选诊断：");
            foreach (var candidate in candidateArray)
            {
                message.AppendLine()
                    .Append("- [")
                    .Append(candidate.Source)
                    .Append("] ")
                    .Append(candidate.CandidatePath)
                    .Append(" — ")
                    .Append(candidate.Status)
                    .Append("：")
                    .Append(candidate.Reason);
            }
        }

        return new CodexExecutableResolution(null, candidateArray, message.ToString());
    }

    private static string? ResolveApplicationDataDirectory(string? applicationDataDirectory)
    {
        var configuredPath = applicationDataDirectory ??
                             Environment.GetEnvironmentVariable("APPDATA") ??
                             Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        try
        {
            return NormalizeDirectory(configuredPath);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return null;
        }
    }

    private static string? ResolveLocalApplicationDataDirectory(
        string? localApplicationDataDirectory)
    {
        var configuredPath = localApplicationDataDirectory ??
                             Environment.GetEnvironmentVariable("LOCALAPPDATA") ??
                             Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        try
        {
            return NormalizeDirectory(configuredPath);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsProbeableExplicitExtension(string extension) =>
        IsCommandScriptExtension(extension) ||
        string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsCommandScriptExtension(string extension) =>
        string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsAppsExecutable(string executablePath)
    {
        if (!string.Equals(
                Path.GetFileName(executablePath),
                "codex.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = Path.GetFullPath(executablePath)
            .Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            string.Equals(segment, "WindowsApps", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumeratePathDirectories(string? pathEnvironment)
    {
        if (string.IsNullOrWhiteSpace(pathEnvironment))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawEntry in pathEnvironment.Split(Path.PathSeparator))
        {
            var trimmed = rawEntry.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            string normalized;
            try
            {
                normalized = NormalizeDirectory(Environment.ExpandEnvironmentVariables(trimmed));
            }
            catch (Exception exception) when (exception is
                ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                continue;
            }

            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
