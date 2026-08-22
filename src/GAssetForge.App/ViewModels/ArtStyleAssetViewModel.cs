using GAssetForge.App.Infrastructure;
using GAssetForge.Core.ArtStyles;
using System.IO;

namespace GAssetForge.App.ViewModels;

public sealed class ArtStyleAssetViewModel : ObservableObject
{
    internal ArtStyleAssetViewModel(ArtStyleAssetModel model, string fullPath)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        FullPath = Path.GetFullPath(fullPath);
    }

    internal ArtStyleAssetModel Model { get; }

    public string Id => Model.Id;

    public string Name => Model.Name;

    public string OriginalFileName => Model.OriginalFileName;

    public string RelativePath => Model.RelativePath;

    public string FullPath { get; }

    public string Extension => Model.Extension.TrimStart('.').ToUpperInvariant();

    public string MediaType => Model.MediaType;

    public string Sha256 => Model.Sha256;

    public long Size => Model.Size;

    public string SizeText => FormatFileSize(Size);

    public DateTimeOffset ImportedAt => Model.ImportedAtUtc.ToLocalTime();

    public bool IsMissing => !File.Exists(FullPath);

    public string AvailabilityText => IsMissing ? "源文件缺失" : SizeText;

    private static string FormatFileSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = Math.Max(0, size);
        var unit = 0;
        var displayValue = (double)value;
        while (displayValue >= 1024 && unit < units.Length - 1)
        {
            displayValue /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} {units[unit]}" : $"{displayValue:0.##} {units[unit]}";
    }
}
