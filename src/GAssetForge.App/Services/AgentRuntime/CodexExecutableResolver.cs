using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace GAssetForge.App.Services.AgentRuntime;

internal sealed record CodexExecutableLaunch(
    string ProcessFileName,
    IReadOnlyList<string> PrefixArguments,
    string CodexExecutablePath,
    bool HasCodeModeCompanion);

internal static class CodexExecutableResolver
{
    internal const string CodeModeHostFileName = "codex-code-mode-host.exe";
    private static readonly TimeSpan LaunchProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<string, bool> LaunchProbeCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static CodexExecutableLaunch Resolve(
        string requestedExecutable,
        string? applicationDirectory = null,
        string? pathEnvironment = null,
        Func<CodexExecutableLaunch, bool>? launchProbe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedExecutable);
        if (!string.Equals(requestedExecutable.Trim(), "codex", StringComparison.OrdinalIgnoreCase))
        {
            return new CodexExecutableLaunch(
                requestedExecutable.Trim(),
                [],
                requestedExecutable.Trim(),
                HasCodeModeCompanion: false);
        }

        var applicationPath = NormalizeDirectory(applicationDirectory ?? AppContext.BaseDirectory);
        var pathDirectories = EnumeratePathDirectories(
                pathEnvironment ?? Environment.GetEnvironmentVariable("PATH"))
            .Where(path => !PathEquals(path, applicationPath))
            .ToArray();
        var rejectedCandidates = new List<string>();
        var canLaunch = launchProbe ?? CanLaunchCandidate;

        // Prefer PATH launchers before raw packaged executables. The official npm shim resolves
        // its own native runtime and remains executable from ordinary desktop processes, while a
        // codex.exe discovered under WindowsApps can exist and still reject direct Process.Start.
        foreach (var directory in pathDirectories)
        {
            var pathShim = TryCreatePathShim(directory);
            if (pathShim is not null && TryAcceptCandidate(pathShim, canLaunch, rejectedCandidates))
            {
                return pathShim;
            }
        }

        foreach (var directory in pathDirectories)
        {
            var completeRuntime = TryCreateCompleteRuntime(directory);
            if (completeRuntime is not null && TryAcceptCandidate(completeRuntime, canLaunch, rejectedCandidates))
            {
                return completeRuntime;
            }
        }

        var completeApplicationRuntime = TryCreateCompleteRuntime(applicationPath);
        if (completeApplicationRuntime is not null &&
            TryAcceptCandidate(completeApplicationRuntime, canLaunch, rejectedCandidates))
        {
            return completeApplicationRuntime;
        }

        foreach (var directory in pathDirectories)
        {
            var legacyExecutable = TryCreateLegacyExecutable(directory);
            if (legacyExecutable is not null &&
                TryAcceptCandidate(legacyExecutable, canLaunch, rejectedCandidates))
            {
                return legacyExecutable;
            }
        }

        var incompleteApplicationExecutable = Path.Combine(applicationPath, "codex.exe");
        if (File.Exists(incompleteApplicationExecutable))
        {
            var missingCompanionPath = Path.Combine(applicationPath, CodeModeHostFileName);
            throw new InvalidOperationException(
                $"检测到不完整的 Codex Runtime：{incompleteApplicationExecutable}。" +
                $"缺少同目录组件：{missingCompanionPath}；该本地副本会遮蔽完整安装。" +
                "请安装完整 Codex Desktop Runtime，或从发行目录移除不完整的 codex.exe 后重试。");
        }

        if (rejectedCandidates.Count > 0)
        {
            throw new InvalidOperationException(
                "检测到 Codex Runtime，但候选程序无法从当前桌面进程启动。" +
                $"已拒绝：{string.Join("；", rejectedCandidates)}。" +
                "请确保 PATH 中存在可执行的 codex.cmd，或重新安装 Codex CLI。");
        }

        throw new FileNotFoundException(
            "未找到可用的 Codex Runtime。请安装 Codex Desktop 或 Codex CLI，并确保其目录已加入 PATH。",
            "codex");
    }

    private static bool TryAcceptCandidate(
        CodexExecutableLaunch candidate,
        Func<CodexExecutableLaunch, bool> launchProbe,
        ICollection<string> rejectedCandidates)
    {
        if (launchProbe(candidate))
        {
            return true;
        }

        rejectedCandidates.Add(candidate.CodexExecutablePath);
        return false;
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

    private static CodexExecutableLaunch? TryCreatePathShim(string directory)
    {
        foreach (var scriptName in new[] { "codex.cmd", "codex.bat" })
        {
            var scriptPath = Path.Combine(directory, scriptName);
            if (!File.Exists(scriptPath))
            {
                continue;
            }

            var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(commandProcessor))
            {
                commandProcessor = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "cmd.exe");
            }

            return new CodexExecutableLaunch(
                commandProcessor,
                ["/d", "/s", "/c", Path.GetFullPath(scriptPath)],
                Path.GetFullPath(scriptPath),
                HasCodeModeCompanion: false);
        }

        return null;
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

    private static bool CanLaunchCandidate(CodexExecutableLaunch candidate)
    {
        var cacheKey = string.Join('\0',
            candidate.ProcessFileName,
            candidate.CodexExecutablePath,
            string.Join('\0', candidate.PrefixArguments));
        return LaunchProbeCache.GetOrAdd(cacheKey, _ => ProbeCandidate(candidate));
    }

    private static bool ProbeCandidate(CodexExecutableLaunch candidate)
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
            foreach (var prefixArgument in candidate.PrefixArguments)
            {
                startInfo.ArgumentList.Add(prefixArgument);
            }

            startInfo.ArgumentList.Add("--version");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit((int)LaunchProbeTimeout.TotalMilliseconds))
            {
                TryKill(process);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is
            Win32Exception or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            return false;
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
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
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
