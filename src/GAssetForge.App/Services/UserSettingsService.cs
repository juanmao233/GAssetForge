using GAssetForge.Core.IconDesign;
using GAssetForge.Core.Imaging;
using System.Text.Json;
using System.IO;

namespace GAssetForge.App.Services;

public enum AppTheme
{
    Dark,
    Light
}

public sealed class AssetGenerationDraftRowSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Description { get; set; } = string.Empty;

    public int Quantity { get; set; } = 1;

    public string? OutputPath { get; set; }
}

public sealed class UserSettingsModel
{
    public int SchemaVersion { get; set; } = 1;
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string ActiveWorkflowKey { get; set; } = "ui";
    public bool HasCompletedWorkflowSelection { get; set; }
    public string? LastProjectPath { get; set; }
    public string? AssetRootPath { get; set; }
    public string? AssetCurrentPath { get; set; }
    public double TilemapGridUnitSize { get; set; } = 64d;
    public int TilemapArtImageWidth { get; set; } = 512;
    public int TilemapArtImageHeight { get; set; } = 256;
    public double TilemapDiamondAngle { get; set; } = 53.13010235415598d;
    public double TilemapRotationAngle { get; set; }
    public string McpAgentKey { get; set; } = "codex";
    public int McpPort { get; set; } = 43120;
    public string? CodexExecutablePath { get; set; }
    public string? CodexThreadId { get; set; }
    public string? CodexThreadWorkingDirectory { get; set; }
    public string AssetGenerationStepKey { get; set; } = "tilemap";
    public string AssetGenerationContentPrompt { get; set; } = "单个游戏场景瓦片";
    public string AssetGenerationNegativePrompt { get; set; } = string.Empty;
    public int AssetGenerationMaxParallelism { get; set; } = 2;
    public bool AssetGenerationAutoRetryEnabled { get; set; } = true;
    public int AssetGenerationAutoRetryCount { get; set; } = 3;
    public bool AssetGenerationPreferUnifiedStylePrompt { get; set; } = true;
    public string? AssetGenerationOutputPath { get; set; }
    public List<AssetGenerationDraftRowSettings> AssetGenerationRows { get; set; } = [];
    public string BackgroundRemovalPresetKey { get; set; } = "magenta";
    public string BackgroundRemovalCustomColorHex { get; set; } = "#FF00FF";
    public double BackgroundRemovalTransparentThreshold { get; set; } = 30d;
    public double BackgroundRemovalOpaqueThreshold { get; set; } = 120d;
    public int BackgroundRemovalEdgeContract { get; set; } = 1;
    public int BackgroundRemovalEdgeFeather { get; set; }
    public bool BackgroundRemovalDespill { get; set; } = true;
    public bool BackgroundRemovalRemoveEnclosedBackground { get; set; } = true;

    // Retained for older GAssetForge builds and persisted task-setting compatibility.
    public bool BackgroundRemovalEdgeConnectedOnly { get; set; } = true;
    public bool BackgroundRemovalUseFakeTransparencyGrid { get; set; }
    public string BackgroundRemovalCheckerColorAHex { get; set; } = "#FFFFFF";
    public string BackgroundRemovalCheckerColorBHex { get; set; } = "#CCCCCC";
    public bool BackgroundRemovalEnableCheckerGridAlignment { get; set; } = true;
    public double BackgroundRemovalCheckerCellSize { get; set; } = 16d;
    public double BackgroundRemovalCheckerOffsetX { get; set; }
    public double BackgroundRemovalCheckerOffsetY { get; set; }
    public double BackgroundRemovalCheckerBoundaryTolerance { get; set; } = 1d;
    public string? BackgroundRemovalLastInputDirectory { get; set; }
    public string? BackgroundRemovalLastOutputDirectory { get; set; }
    public string ImageOutlinePlacement { get; set; } = "Outside";
    public int ImageOutlineWidth { get; set; } = 2;
    public string ImageOutlineColorHex { get; set; } = "#000000";
    public int ImageOutlineAlphaThreshold { get; set; } = 1;
    public bool ImageOutlineExpandCanvas { get; set; } = true;
    public string? ImageOutlineLastInputDirectory { get; set; }
    public string? ImageOutlineLastOutputDirectory { get; set; }
    public string IconDesignModeKey { get; set; } = IconDesignMode.SpecifiedAngle.ToString();
    public int IconDesignImageWidth { get; set; } = 512;
    public int IconDesignImageHeight { get; set; } = 512;
    public double IconDesignCameraAzimuthDegrees { get; set; } = 45d;
    public double IconDesignCameraElevationDegrees { get; set; } = 30d;
    public double IconDesignCameraRollDegrees { get; set; }
    public double IconDesignCameraDistance { get; set; } = 4.5d;
    public double IconDesignFocalLengthMillimeters { get; set; } = 50d;
    public int PixelAlignmentMinimumUpscaleFactor { get; set; } = 10;
    public int PixelAlignmentColumns { get; set; } = 32;
    public int PixelAlignmentRows { get; set; } = 32;
    public string PixelAlignmentSamplingMode { get; set; } = "CenterPixel";
    public bool PixelAlignmentShowGrid { get; set; } = true;
    public bool PixelAlignmentEnableAutomaticColorIndex { get; set; } = true;
    public int PixelAlignmentMaximumIndexedColors { get; set; } = 256;
    public bool PixelAlignmentEnableAspectRatioCorrection { get; set; }
    public double PixelAlignmentTargetAspectRatioA { get; set; } = 1d;
    public double PixelAlignmentTargetAspectRatioB { get; set; } = 1d;
    public bool PixelAlignmentEnableRotationCorrection { get; set; }
    public double PixelAlignmentRotationDegrees { get; set; }
    public bool PixelAlignmentEnablePerspectiveCorrection { get; set; }
    public double PixelAlignmentTargetEdgeAngleDegrees { get; set; } = 90d;
    public string? PixelAlignmentLastInputDirectory { get; set; }
    public string? PixelAlignmentLastOutputDirectory { get; set; }
}

public sealed class UserSettingsService
{
    private readonly WorkspacePaths _paths;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public UserSettingsService(WorkspacePaths paths)
    {
        _paths = paths;
        Paths = paths;
    }

    public WorkspacePaths Paths { get; }

    public UserSettingsModel Load()
    {
        try
        {
            if (!File.Exists(_paths.UserSettingsPath))
            {
                return new UserSettingsModel();
            }

            var settings = JsonSerializer.Deserialize<UserSettingsModel>(File.ReadAllText(_paths.UserSettingsPath), Options);
            if (settings is not { SchemaVersion: 1 } || !Enum.IsDefined(typeof(AppTheme), settings.Theme))
            {
                return new UserSettingsModel();
            }

            settings.AssetGenerationRows ??= [];
            settings.AssetGenerationAutoRetryCount = Math.Clamp(
                settings.AssetGenerationAutoRetryCount,
                0,
                10);
            settings.BackgroundRemovalCheckerCellSize = NormalizeFinite(
                settings.BackgroundRemovalCheckerCellSize,
                16d,
                0.25d,
                4096d);
            settings.BackgroundRemovalCheckerOffsetX = NormalizePositiveOffset(
                settings.BackgroundRemovalCheckerOffsetX,
                settings.BackgroundRemovalCheckerCellSize);
            settings.BackgroundRemovalCheckerOffsetY = NormalizePositiveOffset(
                settings.BackgroundRemovalCheckerOffsetY,
                settings.BackgroundRemovalCheckerCellSize);
            settings.BackgroundRemovalCheckerBoundaryTolerance = NormalizeFinite(
                settings.BackgroundRemovalCheckerBoundaryTolerance,
                1d,
                0d,
                8d);
            settings.ImageOutlineWidth = Math.Clamp(
                settings.ImageOutlineWidth,
                ImageOutlineLimits.MinimumWidth,
                ImageOutlineLimits.MaximumWidth);
            settings.ImageOutlineAlphaThreshold = Math.Clamp(
                settings.ImageOutlineAlphaThreshold,
                ImageOutlineLimits.MinimumAlphaThreshold,
                ImageOutlineLimits.MaximumAlphaThreshold);
            settings.IconDesignImageWidth = Math.Clamp(
                settings.IconDesignImageWidth,
                IconPerspectiveProjectionService.MinimumImageDimension,
                IconPerspectiveProjectionService.MaximumImageDimension);
            settings.IconDesignImageHeight = Math.Clamp(
                settings.IconDesignImageHeight,
                IconPerspectiveProjectionService.MinimumImageDimension,
                IconPerspectiveProjectionService.MaximumImageDimension);
            settings.IconDesignCameraAzimuthDegrees = NormalizeFinite(
                settings.IconDesignCameraAzimuthDegrees,
                45d,
                -180d,
                180d);
            settings.IconDesignCameraElevationDegrees = NormalizeFinite(
                settings.IconDesignCameraElevationDegrees,
                30d,
                IconPerspectiveProjectionService.MinimumElevationDegrees,
                IconPerspectiveProjectionService.MaximumElevationDegrees);
            settings.IconDesignCameraRollDegrees = NormalizeFinite(
                settings.IconDesignCameraRollDegrees,
                0d,
                -180d,
                180d);
            settings.IconDesignCameraDistance = NormalizeFinite(
                settings.IconDesignCameraDistance,
                4.5d,
                IconPerspectiveProjectionService.MinimumCameraDistance,
                20d);
            settings.IconDesignFocalLengthMillimeters = NormalizeFinite(
                settings.IconDesignFocalLengthMillimeters,
                50d,
                IconPerspectiveProjectionService.MinimumFocalLengthMillimeters,
                IconPerspectiveProjectionService.MaximumFocalLengthMillimeters);
            settings.PixelAlignmentMaximumIndexedColors = Math.Clamp(
                settings.PixelAlignmentMaximumIndexedColors,
                PixelAlignmentLimits.MinimumIndexedColorCount,
                PixelAlignmentLimits.MaximumIndexedColorCount);
            return settings;
        }
        catch
        {
            return new UserSettingsModel();
        }
    }

    public void Save(UserSettingsModel settings)
    {
        var temporaryPath = $"{_paths.UserSettingsPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, Options));
        File.Move(temporaryPath, _paths.UserSettingsPath, true);
    }

    private static double NormalizeFinite(
        double value,
        double fallback,
        double minimum,
        double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static double NormalizePositiveOffset(double value, double cellSize)
    {
        if (!double.IsFinite(value) || !double.IsFinite(cellSize) || cellSize <= 0d)
        {
            return 0d;
        }

        var remainder = value % cellSize;
        if (remainder == 0d)
        {
            return 0d;
        }

        return remainder < 0d ? remainder + cellSize : remainder;
    }
}
