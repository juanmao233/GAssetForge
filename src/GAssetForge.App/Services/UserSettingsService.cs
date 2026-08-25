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
    public bool BackgroundRemovalEdgeConnectedOnly { get; set; } = true;
    public string? BackgroundRemovalLastInputDirectory { get; set; }
    public string? BackgroundRemovalLastOutputDirectory { get; set; }
    public int PixelAlignmentMinimumUpscaleFactor { get; set; } = 10;
    public int PixelAlignmentColumns { get; set; } = 32;
    public int PixelAlignmentRows { get; set; } = 32;
    public string PixelAlignmentSamplingMode { get; set; } = "CenterPixel";
    public bool PixelAlignmentShowGrid { get; set; } = true;
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
}
