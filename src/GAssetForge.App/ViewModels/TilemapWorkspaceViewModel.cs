using GAssetForge.App.Infrastructure;
using GAssetForge.Core.Generation;
using System.Windows.Input;

namespace GAssetForge.App.ViewModels;

public sealed class TilemapWorkspaceViewModel : ObservableObject
{
    public const double MinimumGridUnitSize = 8d;
    public const double MaximumGridUnitSize = 512d;
    public const int MinimumArtImageSize = 16;
    public const int MaximumArtImageSize = 8192;
    public const double MinimumDiamondAngle = 15d;
    public const double MaximumDiamondAngle = 165d;
    public const double MinimumRotationAngle = -180d;
    public const double MaximumRotationAngle = 180d;

    public const double DefaultGridUnitSize = 64d;
    public const int DefaultArtImageWidth = 512;
    public const int DefaultArtImageHeight = 256;
    public const double DefaultDiamondAngle = 53.13010235415598d;
    public const double DefaultRotationAngle = 0d;

    private double _gridUnitSize = DefaultGridUnitSize;
    private int _artImageWidth = DefaultArtImageWidth;
    private int _artImageHeight = DefaultArtImageHeight;
    private double _diamondAngle = DefaultDiamondAngle;
    private double _rotationAngle = DefaultRotationAngle;

    public TilemapWorkspaceViewModel()
    {
        ApplyIsometricPresetCommand = new RelayCommand(ApplyIsometricPreset);
        ApplySquarePresetCommand = new RelayCommand(ApplySquarePreset);
        ResetParametersCommand = new RelayCommand(ResetParameters);
    }

    public string DocumentTitle => "Tilemap Scene";

    public string GridPlane => "XY";

    public int MajorLineInterval => 8;

    public string EmptyGridHint => "当前还没有放置瓦片";

    public double GridUnitSize
    {
        get => _gridUnitSize;
        set
        {
            var coerced = CoerceFinite(value, DefaultGridUnitSize, MinimumGridUnitSize, MaximumGridUnitSize);
            if (SetProperty(ref _gridUnitSize, coerced))
            {
                RaiseDerivedPropertiesChanged();
            }
        }
    }

    public int ArtImageWidth
    {
        get => _artImageWidth;
        set
        {
            var coerced = Math.Clamp(value, MinimumArtImageSize, MaximumArtImageSize);
            if (SetProperty(ref _artImageWidth, coerced))
            {
                RaiseDerivedPropertiesChanged();
            }
        }
    }

    public int ArtImageHeight
    {
        get => _artImageHeight;
        set
        {
            var coerced = Math.Clamp(value, MinimumArtImageSize, MaximumArtImageSize);
            if (SetProperty(ref _artImageHeight, coerced))
            {
                RaiseDerivedPropertiesChanged();
            }
        }
    }

    public double DiamondAngle
    {
        get => _diamondAngle;
        set
        {
            var coerced = CoerceFinite(value, DefaultDiamondAngle, MinimumDiamondAngle, MaximumDiamondAngle);
            if (SetProperty(ref _diamondAngle, coerced))
            {
                RaiseDerivedPropertiesChanged();
            }
        }
    }

    public double RotationAngle
    {
        get => _rotationAngle;
        set
        {
            var coerced = CoerceFinite(value, DefaultRotationAngle, MinimumRotationAngle, MaximumRotationAngle);
            if (SetProperty(ref _rotationAngle, coerced))
            {
                RaiseDerivedPropertiesChanged();
            }
        }
    }

    public double ProjectedTileWidth => GetAxes(0d).FootprintWidth;

    public double ProjectedTileHeight => GetAxes(0d).FootprintHeight;

    public double RotatedFootprintWidth
    {
        get
        {
            return GetAxes(RotationAngle).FootprintWidth;
        }
    }

    public double RotatedFootprintHeight
    {
        get
        {
            return GetAxes(RotationAngle).FootprintHeight;
        }
    }

    public double PixelDensityX => ArtImageWidth / ProjectedTileWidth;

    public double PixelDensityY => ArtImageHeight / ProjectedTileHeight;

    public string ArtSizeText => $"{ArtImageWidth} × {ArtImageHeight} px";

    public string ImageAspectRatioText
    {
        get
        {
            var divisor = GreatestCommonDivisor(ArtImageWidth, ArtImageHeight);
            return $"{ArtImageWidth / divisor}:{ArtImageHeight / divisor}";
        }
    }

    public string ProjectedTileSizeText =>
        $"{ProjectedTileWidth:0.##} × {ProjectedTileHeight:0.##} 视图单位";

    public string RotatedFootprintText =>
        $"{RotatedFootprintWidth:0.##} × {RotatedFootprintHeight:0.##} 视图单位";

    public string PixelDensityText =>
        $"X {PixelDensityX:0.###} / Y {PixelDensityY:0.###} px/视图单位";

    public string TileShapeText =>
        Math.Abs(DiamondAngle - 90d) <= 0.000001d ? "正方形" : "菱形";

    public string TileOrientationText =>
        $"{TileShapeText} · A/B 边夹角 {DiamondAngle:0.##}° · A 边方向 {RotationAngle:0.##}°";

    public string GridMeta =>
        $"{GridPlane} · 边长 {GridUnitSize:0.##} · 投影 {ProjectedTileWidth:0.#} × {ProjectedTileHeight:0.#} · 图像 {ArtSizeText}";

    public string StatusText =>
        $"Tilemap · {ArtSizeText} · {TileOrientationText}";

    public string AiGenerationConstraint => BuildGenerationConstraints().Prompt;

    public ICommand ApplyIsometricPresetCommand { get; }

    public ICommand ApplySquarePresetCommand { get; }

    public ICommand ResetParametersCommand { get; }

    public GenerationConstraintPackage BuildGenerationConstraints(
        string contentPrompt = "单个游戏场景瓦片",
        string? stylePrompt = null,
        string? negativePrompt = null,
        int outputCount = 1,
        GenerationBackgroundRemovalSpec? backgroundRemoval = null) =>
        TilemapConstraintPackageBuilder.Build(new TilemapGenerationConstraintInput
        {
            GridUnitSize = GridUnitSize,
            ArtWidth = ArtImageWidth,
            ArtHeight = ArtImageHeight,
            DiamondAngle = DiamondAngle,
            RotationAngle = RotationAngle,
            ContentPrompt = contentPrompt,
            StylePrompt = stylePrompt,
            NegativePrompt = negativePrompt,
            OutputCount = outputCount,
            BackgroundRemoval = backgroundRemoval
        });

    private void ApplyIsometricPreset()
    {
        ArtImageWidth = 512;
        ArtImageHeight = 256;
        DiamondAngle = DefaultDiamondAngle;
        RotationAngle = 0d;
    }

    private void ApplySquarePreset()
    {
        ArtImageWidth = 512;
        ArtImageHeight = 512;
        DiamondAngle = 90d;
        RotationAngle = 0d;
    }

    private void ResetParameters()
    {
        GridUnitSize = DefaultGridUnitSize;
        ArtImageWidth = DefaultArtImageWidth;
        ArtImageHeight = DefaultArtImageHeight;
        DiamondAngle = DefaultDiamondAngle;
        RotationAngle = DefaultRotationAngle;
    }

    private TilemapAxisBasis GetAxes(double rotationAngle) =>
        TilemapGeometryCalculator.CalculateAxes(GridUnitSize, DiamondAngle, rotationAngle);

    private void RaiseDerivedPropertiesChanged()
    {
        OnPropertyChanged(nameof(ProjectedTileWidth));
        OnPropertyChanged(nameof(ProjectedTileHeight));
        OnPropertyChanged(nameof(RotatedFootprintWidth));
        OnPropertyChanged(nameof(RotatedFootprintHeight));
        OnPropertyChanged(nameof(PixelDensityX));
        OnPropertyChanged(nameof(PixelDensityY));
        OnPropertyChanged(nameof(ArtSizeText));
        OnPropertyChanged(nameof(ImageAspectRatioText));
        OnPropertyChanged(nameof(ProjectedTileSizeText));
        OnPropertyChanged(nameof(RotatedFootprintText));
        OnPropertyChanged(nameof(PixelDensityText));
        OnPropertyChanged(nameof(TileShapeText));
        OnPropertyChanged(nameof(TileOrientationText));
        OnPropertyChanged(nameof(GridMeta));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AiGenerationConstraint));
    }

    private static double CoerceFinite(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Max(1, Math.Abs(left));
    }
}
