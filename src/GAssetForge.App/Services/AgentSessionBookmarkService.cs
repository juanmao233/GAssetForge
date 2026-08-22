using System.IO;

namespace GAssetForge.App.Services;

internal static class AgentSessionBookmarkService
{
    public static string? ResolveResumeThreadId(
        UserSettingsModel settings,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (string.IsNullOrWhiteSpace(settings.CodexThreadId) ||
            string.IsNullOrWhiteSpace(settings.CodexThreadWorkingDirectory))
        {
            return null;
        }

        return PathEquals(settings.CodexThreadWorkingDirectory, workingDirectory)
            ? settings.CodexThreadId.Trim()
            : null;
    }

    public static bool ClearIfWorkingDirectoryChanged(
        UserSettingsModel settings,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (string.IsNullOrWhiteSpace(settings.CodexThreadId) &&
            string.IsNullOrWhiteSpace(settings.CodexThreadWorkingDirectory))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(settings.CodexThreadId) &&
            !string.IsNullOrWhiteSpace(settings.CodexThreadWorkingDirectory) &&
            PathEquals(settings.CodexThreadWorkingDirectory, workingDirectory))
        {
            return false;
        }

        Clear(settings);
        return true;
    }

    public static void Store(
        UserSettingsModel settings,
        string threadId,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        settings.CodexThreadId = threadId.Trim();
        settings.CodexThreadWorkingDirectory = NormalizePath(workingDirectory);
    }

    public static void Clear(UserSettingsModel settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.CodexThreadId = null;
        settings.CodexThreadWorkingDirectory = null;
    }

    public static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(
                NormalizePath(left),
                NormalizePath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            NotSupportedException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
}
