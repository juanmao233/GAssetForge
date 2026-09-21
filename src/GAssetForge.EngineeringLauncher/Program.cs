using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GAssetForge.EngineeringLauncher;

internal static class Program
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    [STAThread]
    private static int Main(string[] arguments)
    {
        var options = EngineeringLauncherOptions.Parse(arguments);
        EngineeringLaunchPlan plan;
        try
        {
            plan = EngineeringLaunchPlan.Discover(AppContext.BaseDirectory);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
            return 1;
        }

        if (options.VerifyOnly)
        {
            return 0;
        }

        using var launchMutex = new Mutex(
            initiallyOwned: false,
            CreateMutexName(plan.RepositoryRoot));
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = launchMutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                ShowError("另一个 GAssetForge 工程启动器正在构建，请等待它完成。");
                return 2;
            }

            var buildResult = BuildCurrentWorkspace(plan);
            WriteBuildLog(plan, buildResult);
            if (!buildResult.IsSuccess)
            {
                ShowError(
                    $"当前源码构建失败，未启动旧版程序。\n\n{buildResult.Summary}\n\n" +
                    $"完整日志：{plan.LogPath}");
                return 3;
            }

            if (!File.Exists(plan.AppExecutablePath))
            {
                ShowError(
                    "构建已完成，但没有找到桌面程序；不会回退到 dist。\n\n" +
                    $"预期路径：{plan.AppExecutablePath}\n日志：{plan.LogPath}");
                return 4;
            }

            if (options.BuildOnly)
            {
                return 0;
            }

            try
            {
                Process.Start(plan.CreateApplicationStartInfo(options.ForwardedArguments));
                return 0;
            }
            catch (Exception exception) when (exception is
                Win32Exception or
                InvalidOperationException or
                IOException or
                UnauthorizedAccessException)
            {
                ShowError(
                    $"当前工程已构建，但桌面程序启动失败：{exception.Message}\n\n" +
                    $"程序路径：{plan.AppExecutablePath}\n日志：{plan.LogPath}");
                return 5;
            }
        }
        finally
        {
            if (ownsMutex)
            {
                try
                {
                    launchMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }
        }
    }

    private static EngineeringBuildResult BuildCurrentWorkspace(EngineeringLaunchPlan plan)
    {
        var transcript = new StringBuilder();
        foreach (var startInfo in plan.CreateBuildStartInfos(ResolveDotnetHost()))
        {
            transcript.AppendLine($"> {startInfo.FileName} {FormatArguments(startInfo.ArgumentList)}");
            try
            {
                using var process = new Process { StartInfo = startInfo };
                if (!process.Start())
                {
                    return EngineeringBuildResult.Failed(
                        "dotnet build 未能启动。",
                        transcript.ToString());
                }

                var standardOutput = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit((int)BuildTimeout.TotalMilliseconds))
                {
                    TryKill(process);
                    return EngineeringBuildResult.Failed(
                        $"构建超过 {BuildTimeout.TotalMinutes:0} 分钟，已终止。",
                        transcript.ToString());
                }

                Task.WaitAll(standardOutput, standardError);
                transcript.AppendLine(standardOutput.Result);
                if (!string.IsNullOrWhiteSpace(standardError.Result))
                {
                    transcript.AppendLine("[stderr]");
                    transcript.AppendLine(standardError.Result);
                }

                transcript.AppendLine($"[exit-code] {process.ExitCode}");
                if (process.ExitCode != 0)
                {
                    return EngineeringBuildResult.Failed(
                        $"{Path.GetFileName(startInfo.ArgumentList[1])} 构建失败（退出码 {process.ExitCode}）。",
                        transcript.ToString());
                }
            }
            catch (Exception exception) when (exception is
                Win32Exception or
                InvalidOperationException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException)
            {
                transcript.AppendLine($"[exception] {exception}");
                return EngineeringBuildResult.Failed(
                    $"无法执行 dotnet build：{exception.Message}",
                    transcript.ToString());
            }
        }

        return EngineeringBuildResult.Succeeded(transcript.ToString());
    }

    private static void WriteBuildLog(
        EngineeringLaunchPlan plan,
        EngineeringBuildResult result)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.LogPath)!);
            File.WriteAllText(
                plan.LogPath,
                $"GAssetForge engineering launcher\n" +
                $"utc={DateTimeOffset.UtcNow:O}\n" +
                $"repository={plan.RepositoryRoot}\n" +
                $"success={result.IsSuccess}\n" +
                $"summary={result.Summary}\n\n" +
                result.Transcript,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
        }
    }

    private static string ResolveDotnetHost()
    {
        var configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrWhiteSpace(configuredHost) && File.Exists(configuredHost)
            ? Path.GetFullPath(configuredHost)
            : "dotnet";
    }

    private static string CreateMutexName(string repositoryRoot)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(repositoryRoot).ToUpperInvariant()));
        return $"Local\\GAssetForge.EngineeringLauncher.{Convert.ToHexString(digest.AsSpan(0, 8))}";
    }

    private static string FormatArguments(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(argument =>
            argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument));

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

    private static void ShowError(string message) =>
        _ = MessageBox(
            nint.Zero,
            message,
            "GAssetForge 工程启动器",
            0x00000010u | 0x00010000u);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(
        nint windowHandle,
        string text,
        string caption,
        uint type);
}

internal sealed record EngineeringLauncherOptions(
    bool VerifyOnly,
    bool BuildOnly,
    IReadOnlyList<string> ForwardedArguments)
{
    internal static EngineeringLauncherOptions Parse(IEnumerable<string> arguments)
    {
        var verifyOnly = false;
        var buildOnly = false;
        var forwarded = new List<string>();
        foreach (var argument in arguments)
        {
            if (string.Equals(argument, "--verify-only", StringComparison.OrdinalIgnoreCase))
            {
                verifyOnly = true;
            }
            else if (string.Equals(argument, "--build-only", StringComparison.OrdinalIgnoreCase))
            {
                buildOnly = true;
            }
            else
            {
                forwarded.Add(argument);
            }
        }

        return new EngineeringLauncherOptions(verifyOnly, buildOnly, forwarded);
    }
}

internal sealed record EngineeringBuildResult(
    bool IsSuccess,
    string Summary,
    string Transcript)
{
    internal static EngineeringBuildResult Succeeded(string transcript) =>
        new(true, "当前 App 与 MCP Server 构建成功。", transcript);

    internal static EngineeringBuildResult Failed(string summary, string transcript) =>
        new(false, summary, transcript);
}
