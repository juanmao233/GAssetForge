using GAssetForge.EngineeringLauncher;
using System.IO;

namespace GAssetForge.SmokeTests;

internal static class EngineeringLauncherSmokeTests
{
    public static void Run(string temporaryRoot)
    {
        var repositoryRoot = Path.Combine(temporaryRoot, "engineering-launcher-repository");
        var nestedStart = Path.Combine(repositoryRoot, "artifacts", "engineering-launcher", "publish");
        var appProject = Path.Combine(
            repositoryRoot,
            "src",
            "GAssetForge.App",
            "GAssetForge.App.csproj");
        var mcpProject = Path.Combine(
            repositoryRoot,
            "src",
            "GAssetForge.McpServer",
            "GAssetForge.McpServer.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(appProject)!);
        Directory.CreateDirectory(Path.GetDirectoryName(mcpProject)!);
        Directory.CreateDirectory(nestedStart);
        File.WriteAllText(Path.Combine(repositoryRoot, "GAssetForge.sln"), string.Empty);
        File.WriteAllText(appProject, string.Empty);
        File.WriteAllText(mcpProject, string.Empty);

        var plan = EngineeringLaunchPlan.Discover(nestedStart);
        Equal(Path.GetFullPath(repositoryRoot), plan.RepositoryRoot,
            "工程启动器应从发布子目录向上发现正式仓库根");
        False(plan.AppExecutablePath.Contains(
                $"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase),
            "工程启动器不得读取或回退 dist");
        Equal(
            Path.Combine(
                repositoryRoot,
                "src",
                "GAssetForge.App",
                "bin",
                "Debug",
                "net8.0-windows",
                "GAssetForge.exe"),
            plan.AppExecutablePath,
            "工程启动器应只启动当前 Debug apphost");

        var builds = plan.CreateBuildStartInfos("dotnet-fixture");
        Equal(2, builds.Count, "工程启动器必须构建 App 与 MCP Server");
        Equal(appProject, builds[0].ArgumentList[1], "首个工程构建目标");
        Equal(mcpProject, builds[1].ArgumentList[1], "第二个工程构建目标");
        True(builds.All(build =>
                string.Equals(build.WorkingDirectory, repositoryRoot, StringComparison.OrdinalIgnoreCase) &&
                build.RedirectStandardOutput &&
                build.RedirectStandardError &&
                !build.ArgumentList.Any(argument => argument.Contains("dist", StringComparison.OrdinalIgnoreCase))),
            "工程构建必须固定仓库工作目录并与 dist 完全解耦");

        var appStart = plan.CreateApplicationStartInfo(["--fixture"]);
        Equal(plan.AppExecutablePath, appStart.FileName, "桌面启动目标");
        Equal(repositoryRoot, appStart.WorkingDirectory, "桌面启动工作目录");
        Equal("--fixture", appStart.ArgumentList.Single(), "桌面参数应安全透传");

        Throws<InvalidOperationException>(
            () => EngineeringLaunchPlan.Discover(Path.Combine(temporaryRoot, "not-a-repository")),
            "非仓库位置必须 fail-closed，不能寻找旧 dist");
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new EngineeringLauncherSmokeTestException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new EngineeringLauncherSmokeTestException(
                $"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new EngineeringLauncherSmokeTestException(message);
    }

    private sealed class EngineeringLauncherSmokeTestException(string message) : Exception(message);
}
