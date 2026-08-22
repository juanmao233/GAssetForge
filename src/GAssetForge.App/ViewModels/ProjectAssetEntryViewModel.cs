using GAssetForge.App.Services;

namespace GAssetForge.App.ViewModels;

public sealed class ProjectAssetEntryViewModel(LocalAssetEntry entry)
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"
    };

    public string Name => entry.Name;

    public string FullPath => entry.FullPath;

    public bool IsDirectory => entry.IsDirectory;

    public string Extension => entry.Extension;

    public string IconGlyph => IsDirectory ? "\uE8B7" : ImageExtensions.Contains(Extension) ? "\uEB9F" : "\uE8A5";

    public string KindLabel => IsDirectory
        ? "文件夹"
        : ImageExtensions.Contains(Extension)
            ? "图像"
            : Extension.ToLowerInvariant() switch
            {
                ".psd" => "Photoshop",
                ".json" => "JSON",
                ".prefab" or ".asset" => "资产",
                "" => "文件",
                _ => Extension.TrimStart('.').ToUpperInvariant()
            };

    public string SizeText => IsDirectory ? "—" : FormatSize(entry.Size);

    public string ModifiedText => entry.ModifiedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024L * 1024L => $"{bytes / (1024d * 1024d):0.#} MB",
        _ => $"{bytes / (1024d * 1024d * 1024d):0.#} GB"
    };
}
