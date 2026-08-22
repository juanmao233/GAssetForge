using System.IO;

namespace GAssetForge.App.Services;

public sealed record LocalAssetEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    string Extension,
    long Size,
    DateTimeOffset ModifiedAt);

public sealed record LocalAssetCatalogSnapshot(
    IReadOnlyList<LocalAssetEntry> Entries,
    int DirectoryCount,
    int FileCount);

public sealed class LocalAssetCatalogService
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
    };

    public LocalAssetCatalogSnapshot Enumerate(string directoryPath)
    {
        var directory = new DirectoryInfo(directoryPath);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"资产目录不存在：{directoryPath}");
        }

        var directories = directory
            .EnumerateDirectories("*", Options)
            .Select(folder => new LocalAssetEntry(
                folder.Name,
                folder.FullName,
                true,
                string.Empty,
                0,
                folder.LastWriteTimeUtc))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var files = directory
            .EnumerateFiles("*", Options)
            .Select(file => new LocalAssetEntry(
                file.Name,
                file.FullName,
                false,
                file.Extension,
                file.Length,
                file.LastWriteTimeUtc))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LocalAssetCatalogSnapshot(
            directories.Concat(files).ToList(),
            directories.Count,
            files.Count);
    }

    public bool HasSubdirectories(string directoryPath)
    {
        try
        {
            return Directory.EnumerateDirectories(directoryPath, "*", Options).Any();
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or
            IOException or
            DirectoryNotFoundException or
            PathTooLongException)
        {
            return false;
        }
    }
}
