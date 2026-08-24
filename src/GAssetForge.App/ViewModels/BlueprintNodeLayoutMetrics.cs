using System.Windows;

namespace GAssetForge.App.ViewModels;

public static class BlueprintNodeLayoutMetrics
{
    public const double HeaderHeight = 40d;

    public const double TagsRowHeight = 36d;

    public const double PortRowHeight = 27d;

    public static GridLength HeaderGridLength { get; } = new(HeaderHeight);

    public static GridLength TagsRowGridLength { get; } = new(TagsRowHeight);

    public const double FirstPortCenterY =
        1d + HeaderHeight + TagsRowHeight + PortRowHeight / 2d;
}
