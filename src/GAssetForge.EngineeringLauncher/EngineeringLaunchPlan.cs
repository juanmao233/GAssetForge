using System.Diagnostics;

namespace GAssetForge.EngineeringLauncher;

internal sealed record EngineeringLaunchPlan(
    string RepositoryRoot,
    string AppProjectPath,
    string McpServerProjectPath,
    string AppExecutablePath,
    string LogPath)
{
    private const string Configuration = "Debug";

    internal static EngineeringLaunchPlan Discover(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        var repositoryRoot = FindRepositoryRoot(startDirectory) ??
                             throw new InvalidOperationException(
                                 "启动器必须放在 GAssetForge 仓库内；未找到 GAssetForge.sln 和正式项目文件。");
        var appProjectPath = Path.Combine(
            repositoryRoot,
            "src",
            "GAssetForge.App",
            "GAssetForge.App.csproj");
        var mcpServerProjectPath = Path.Combine(
            repositoryRoot,
            "src",
            "GAssetForge.McpServer",
            "GAssetForge.McpServer.csproj");
        var appExecutablePath = Path.Combine(
            repositoryRoot,
            "src",
            "GAssetForge.App",
            "bin",
            Configuration,
            "net8.0-windows",
            "GAssetForge.exe");
        var logPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "engineering-launcher",
            "logs",
            "launcher-latest.log");

        return new EngineeringLaunchPlan(
            repositoryRoot,
            appProjectPath,
            mcpServerProjectPath,
            appExecutablePath,
            logPath);
    }

    internal IReadOnlyList<ProcessStartInfo> CreateBuildStartInfos(string dotnetHost) =>
    [
        CreateBuildStartInfo(dotnetHost, AppProjectPath),
        CreateBuildStartInfo(dotnetHost, McpServerProjectPath)
    ];

    internal ProcessStartInfo CreateApplicationStartInfo(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = AppExecutablePath,
            WorkingDirectory = RepositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["GASSETFORGE_ENGINEERING_ENTRY"] = "1";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private ProcessStartInfo CreateBuildStartInfo(string dotnetHost, string projectPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetHost,
            WorkingDirectory = RepositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(Configuration);
        startInfo.ArgumentList.Add("--nologo");
        return startInfo;
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return null;
        }

        while (current is not null)
        {
            var solutionPath = Path.Combine(current.FullName, "GAssetForge.sln");
            var appProjectPath = Path.Combine(
                current.FullName,
                "src",
                "GAssetForge.App",
                "GAssetForge.App.csproj");
            var mcpProjectPath = Path.Combine(
                current.FullName,
                "src",
                "GAssetForge.McpServer",
                "GAssetForge.McpServer.csproj");
            if (File.Exists(solutionPath) &&
                File.Exists(appProjectPath) &&
                File.Exists(mcpProjectPath))
            {
                return Path.TrimEndingDirectorySeparator(current.FullName);
            }

            current = current.Parent;
        }

        return null;
    }
}
