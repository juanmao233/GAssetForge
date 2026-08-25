using System.Globalization;
using System.Text.Json.Nodes;

namespace GAssetForge.Core.Generation;

public sealed record TilemapGenerationConstraintInput
{
    public const double DefaultGridUnitSize = 64d;
    public const int DefaultArtWidth = 512;
    public const int DefaultArtHeight = 256;
    public const double DefaultDiamondAngle = 53.13010235415598d;

    public double GridUnitSize { get; init; } = DefaultGridUnitSize;

    public int ArtWidth { get; init; } = DefaultArtWidth;

    public int ArtHeight { get; init; } = DefaultArtHeight;

    public double DiamondAngle { get; init; } = DefaultDiamondAngle;

    /// <summary>
    /// Screen-space rotation in degrees. Positive values rotate clockwise, matching the WPF editor.
    /// </summary>
    public double RotationAngle { get; init; }

    public string ContentPrompt { get; init; } = "单个游戏场景瓦片";

    public string? StylePrompt { get; init; }

    public string? NegativePrompt { get; init; }

    /// <summary>
    /// Number of visually distinct variants requested for this one content description.
    /// </summary>
    public int OutputCount { get; init; } = 1;

    public GenerationBackgroundRemovalSpec? BackgroundRemoval { get; init; }
}

public sealed record TilemapGenerationMetrics
{
    public double ProjectedTileWidth { get; init; }

    public double ProjectedTileHeight { get; init; }

    public double RotatedFootprintWidth { get; init; }

    public double RotatedFootprintHeight { get; init; }

    public double PixelDensityX { get; init; }

    public double PixelDensityY { get; init; }

    public double AxisAX { get; init; }

    public double AxisAY { get; init; }

    public double AxisBX { get; init; }

    public double AxisBY { get; init; }

    public string ImageAspectRatio { get; init; } = string.Empty;
}

public static class TilemapConstraintPackageBuilder
{
    public const string WorkflowId = "tilemap";
    public const string PreferredSkill = "imagegen";
    public const string RotationConvention =
        TilemapGeometryCalculator.AxisAAlignedRotationConventionId;
    public const string SolidOpaqueColorBackgroundMode = "solid-opaque-color";
    public const string TransparentAlphaBackgroundMode = "transparent-alpha";
    public const string TransparentBackgroundRule =
        "生成内容必须完整落在瓦片有效轮廓内；有效轮廓外 alpha 必须为 0。透明背景必须是真实 alpha 通道，不得使用白底、纯色底或棋盘格模拟透明。";

    public static GenerationConstraintPackage Build(TilemapGenerationConstraintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);

        var metrics = CalculateMetrics(input);
        var artSize = $"{input.ArtWidth} × {input.ArtHeight} px";
        var backgroundInstruction = BuildBackgroundInstruction(input.BackgroundRemoval);
        var prompt = BuildPrompt(input, metrics, artSize);
        var outputCountInstruction = input.OutputCount == 1
            ? $"输出必须为单张 {artSize} PNG，保持精确宽高比 {metrics.ImageAspectRatio}。"
            : $"输出必须为 {input.OutputCount} 张 {artSize} PNG；每张都保持精确宽高比 {metrics.ImageAspectRatio}，" +
              "并作为同一物品描述下可辨识的美术变体，不得复制同一图片冒充多个产物。";

        var parameters = new JsonObject
        {
            ["tilemap"] = new JsonObject
            {
                ["gridPlane"] = "XY",
                ["gridUnitSize"] = input.GridUnitSize,
                ["artWidth"] = input.ArtWidth,
                ["artHeight"] = input.ArtHeight,
                ["diamondAngle"] = input.DiamondAngle,
                ["rotationAngle"] = input.RotationAngle,
                ["rotationConvention"] = RotationConvention,
                ["projectedTileWidth"] = metrics.ProjectedTileWidth,
                ["projectedTileHeight"] = metrics.ProjectedTileHeight,
                ["rotatedFootprintWidth"] = metrics.RotatedFootprintWidth,
                ["rotatedFootprintHeight"] = metrics.RotatedFootprintHeight,
                ["pixelDensityX"] = metrics.PixelDensityX,
                ["pixelDensityY"] = metrics.PixelDensityY,
                ["imageAspectRatio"] = metrics.ImageAspectRatio,
                ["axisA"] = new JsonObject
                {
                    ["x"] = metrics.AxisAX,
                    ["y"] = metrics.AxisAY
                },
                ["axisB"] = new JsonObject
                {
                    ["x"] = metrics.AxisBX,
                    ["y"] = metrics.AxisBY
                }
            },
            ["background"] = BuildBackgroundParameters(input.BackgroundRemoval)
        };

        var package = new GenerationConstraintPackage
        {
            WorkflowId = WorkflowId,
            PreferredSkill = PreferredSkill,
            Prompt = prompt,
            NegativePrompt = input.NegativePrompt,
            Output = new GenerationOutputSpec
            {
                Width = input.ArtWidth,
                Height = input.ArtHeight,
                Count = input.OutputCount,
                Format = "png",
                TransparentBackground = true,
                BackgroundRemoval = input.BackgroundRemoval
            },
            Rules =
            [
                new GenerationConstraintRule
                {
                    Key = "tilemap.canvas.exact-size",
                    Category = "output",
                    Instruction = outputCountInstruction
                },
                new GenerationConstraintRule
                {
                    Key = "tilemap.geometry.diamond",
                    Category = "perspective",
                    Instruction =
                        $"瓦片有效轮廓为控制角 {Format(input.DiamondAngle)}° 的{GetTileShapeName(input.DiamondAngle)}；" +
                        $"未旋转投影尺寸为 {Format(metrics.ProjectedTileWidth)} × {Format(metrics.ProjectedTileHeight)} 视图单位。" +
                        BuildEdgeDirectionInstruction(metrics)
                },
                new GenerationConstraintRule
                {
                    Key = "tilemap.geometry.rotation",
                    Category = "perspective",
                    Instruction =
                        $"以屏幕坐标 A/X 轴向右为 0° 基准，顺时针为正，将瓦片整体旋转 {Format(input.RotationAngle)}°；" +
                        "控制角只定义边 A 与边 B 的夹角，不得被再次叠加为画布旋转。" +
                        $"旋转后包围盒为 {Format(metrics.RotatedFootprintWidth)} × {Format(metrics.RotatedFootprintHeight)} 视图单位。"
                },
                new GenerationConstraintRule
                {
                    Key = "tilemap.background.transparent",
                    Category = "alpha",
                    Instruction = backgroundInstruction
                }
            ],
            Parameters = parameters
        };

        GenerationInputValidator.ValidateConstraintPackage(package);
        return package;
    }

    public static TilemapGenerationMetrics CalculateMetrics(TilemapGenerationConstraintInput input) =>
        CalculateMetrics(input, TilemapRotationConvention.AxisAAligned);

    public static TilemapGenerationMetrics CalculateMetrics(
        TilemapGenerationConstraintInput input,
        TilemapRotationConvention rotationConvention)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateGeometry(input);

        var projectedAxes = TilemapGeometryCalculator.CalculateAxes(
            input.GridUnitSize,
            input.DiamondAngle,
            0d,
            rotationConvention);
        var rotatedAxes = TilemapGeometryCalculator.CalculateAxes(
            input.GridUnitSize,
            input.DiamondAngle,
            input.RotationAngle,
            rotationConvention);
        var divisor = GreatestCommonDivisor(input.ArtWidth, input.ArtHeight);

        return new TilemapGenerationMetrics
        {
            ProjectedTileWidth = projectedAxes.FootprintWidth,
            ProjectedTileHeight = projectedAxes.FootprintHeight,
            RotatedFootprintWidth = rotatedAxes.FootprintWidth,
            RotatedFootprintHeight = rotatedAxes.FootprintHeight,
            PixelDensityX = input.ArtWidth / projectedAxes.FootprintWidth,
            PixelDensityY = input.ArtHeight / projectedAxes.FootprintHeight,
            AxisAX = rotatedAxes.AxisAX,
            AxisAY = rotatedAxes.AxisAY,
            AxisBX = rotatedAxes.AxisBX,
            AxisBY = rotatedAxes.AxisBY,
            ImageAspectRatio = $"{input.ArtWidth / divisor}:{input.ArtHeight / divisor}"
        };
    }

    private static string BuildPrompt(
        TilemapGenerationConstraintInput input,
        TilemapGenerationMetrics metrics,
        string artSize)
    {
        var style = input.StylePrompt is null ? string.Empty : $" 美术风格：{input.StylePrompt}。";
        var subject = input.OutputCount == 1
            ? "生成单个游戏 Tilemap 瓦片资产。"
            : $"生成 {input.OutputCount} 个同类但可辨识的游戏 Tilemap 瓦片变体，不得复制同一图片冒充多个产物。";
        return $"{input.ContentPrompt}。{style}" +
               subject +
               $"AI 每个单瓦片输出画布必须为 {artSize}（宽高比 {metrics.ImageAspectRatio}）。" +
               $"Tilemap 视图中瓦片边长为 {Format(input.GridUnitSize)}，控制角为 {Format(input.DiamondAngle)}°，" +
               $"未旋转投影占地 {Format(metrics.ProjectedTileWidth)} × {Format(metrics.ProjectedTileHeight)} 视图单位，" +
               $"以屏幕坐标 A/X 轴向右为 0° 基准，整体顺时针旋转 {Format(input.RotationAngle)}° 后占地 {Format(metrics.RotatedFootprintWidth)} × {Format(metrics.RotatedFootprintHeight)} 视图单位。" +
               BuildOrientationInstruction(input, metrics) +
               BuildBackgroundInstruction(input.BackgroundRemoval);
    }

    private static string BuildOrientationInstruction(
        TilemapGenerationConstraintInput input,
        TilemapGenerationMetrics metrics)
    {
        var shapeDefinition = IsSquareAngle(input.DiamondAngle)
            ? $"控制角 {Format(input.DiamondAngle)}° 表示正方形；它只定义边 A 与边 B 的夹角，不代表整体朝向。"
            : $"控制角 {Format(input.DiamondAngle)}° 表示等边平行四边形（菱形）的边 A/B 夹角，不代表整体朝向。";
        var axisAlignedSquareInstruction =
            IsSquareAngle(input.DiamondAngle) && IsZeroRotation(input.RotationAngle)
                ? "当前有效轮廓为画布轴对齐正方形：上下边水平、左右边垂直。" +
                  "禁止额外旋转 45°，禁止生成角点朝上的钻石形（on-point diamond）。" +
                  "画布为正方形并不等于主体合格：前景瓦片主体的外接包围盒也必须近似正方形，像素宽度与高度近似相等，" +
                  "不得呈竖向或横向长方形；主体建议占据画布较短边的 80%～90%，居中且四条边完整。" +
                  "瓦片主体必须覆盖该方形有效区，仅允许少量纯色安全边框。"
                : "必须严格按上述两条边方向形成有效轮廓，不得把控制角再次叠加为额外旋转。";
        return shapeDefinition +
               BuildEdgeDirectionInstruction(metrics) +
               axisAlignedSquareInstruction +
               "几何约束优先于风格参考图；参考图只能影响材质、配色和笔触，不得改变瓦片外轮廓或朝向。";
    }

    private static string BuildEdgeDirectionInstruction(TilemapGenerationMetrics metrics)
    {
        var axisAAngle = CalculateScreenClockwiseAngle(metrics.AxisAX, metrics.AxisAY);
        var axisBAngle = CalculateScreenClockwiseAngle(metrics.AxisBX, metrics.AxisBY);
        return
            $"边 A 相对屏幕 X 轴顺时针 {Format(axisAAngle)}°（向量 {Format(metrics.AxisAX)}, {Format(metrics.AxisAY)}），" +
            $"边 B 相对屏幕 X 轴顺时针 {Format(axisBAngle)}°（向量 {Format(metrics.AxisBX)}, {Format(metrics.AxisBY)}）。";
    }

    private static string BuildBackgroundInstruction(GenerationBackgroundRemovalSpec? backgroundRemoval) =>
        backgroundRemoval is null
            ? TransparentBackgroundRule
            : $"生成阶段必须使用均匀纯色 {backgroundRemoval.ColorHex} 背景，画布边缘和瓦片有效轮廓外均保持该颜色；" +
              "不得使用渐变、纹理、棋盘格、背景投影或环境雾污染底色，也不要自行抠图或制造透明通道。" +
              "GAssetForge 会把该原图作为第一道工序输入，在本地剔除底色并交付最终真实透明 PNG；最终瓦片有效轮廓外 alpha 必须为 0。";

    private static JsonObject BuildBackgroundParameters(
        GenerationBackgroundRemovalSpec? backgroundRemoval)
    {
        var generationStage = backgroundRemoval is null
            ? new JsonObject
            {
                ["mode"] = TransparentAlphaBackgroundMode,
                ["outsideDiamondAlpha"] = 0
            }
            : new JsonObject
            {
                ["mode"] = SolidOpaqueColorBackgroundMode,
                ["colorHex"] = backgroundRemoval.ColorHex,
                ["alpha"] = 255
            };

        return new JsonObject
        {
            ["generationStage"] = generationStage,
            ["finalStage"] = new JsonObject
            {
                ["mode"] = TransparentAlphaBackgroundMode,
                ["outsideDiamondAlpha"] = 0
            }
        };
    }

    private static void Validate(TilemapGenerationConstraintInput input)
    {
        ValidateGeometry(input);
        ValidatePromptPart(input.ContentPrompt, nameof(input.ContentPrompt), 16_384);

        if (input.StylePrompt is not null)
        {
            ValidatePromptPart(input.StylePrompt, nameof(input.StylePrompt), 8_192);
        }

        if (input.NegativePrompt is not null)
        {
            ValidatePromptPart(input.NegativePrompt, nameof(input.NegativePrompt), 32_768);
        }

        if (input.OutputCount is < 1 or > GenerationInputValidator.MaximumOutputCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input.OutputCount),
                $"单行输出数量必须位于 1 到 {GenerationInputValidator.MaximumOutputCount} 之间。");
        }
    }

    private static void ValidateGeometry(TilemapGenerationConstraintInput input)
    {
        if (!double.IsFinite(input.GridUnitSize) || input.GridUnitSize is < 8d or > 512d)
        {
            throw new ArgumentOutOfRangeException(nameof(input.GridUnitSize), "网格边长必须位于 8 到 512 之间。");
        }

        if (input.ArtWidth is < 16 or > 8_192)
        {
            throw new ArgumentOutOfRangeException(nameof(input.ArtWidth), "美术图片宽度必须位于 16 到 8192 像素之间。");
        }

        if (input.ArtHeight is < 16 or > 8_192)
        {
            throw new ArgumentOutOfRangeException(nameof(input.ArtHeight), "美术图片高度必须位于 16 到 8192 像素之间。");
        }

        if (!double.IsFinite(input.DiamondAngle) || input.DiamondAngle is < 15d or > 165d)
        {
            throw new ArgumentOutOfRangeException(nameof(input.DiamondAngle), "瓦片控制角必须位于 15° 到 165° 之间。");
        }

        if (!double.IsFinite(input.RotationAngle) || input.RotationAngle is < -180d or > 180d)
        {
            throw new ArgumentOutOfRangeException(nameof(input.RotationAngle), "旋转角必须位于 -180° 到 180° 之间。");
        }
    }

    private static void ValidatePromptPart(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(character => character == '\0'))
        {
            throw new ArgumentException($"{parameterName} 为空、过长或包含无效字符。", parameterName);
        }
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Max(1, Math.Abs(left));
    }

    private static bool IsSquareAngle(double angle) => Math.Abs(angle - 90d) <= 0.000001d;

    private static bool IsZeroRotation(double angle) => Math.Abs(angle) <= 0.000001d;

    private static string GetTileShapeName(double angle) =>
        IsSquareAngle(angle) ? "正方形" : "菱形";

    private static double CalculateScreenClockwiseAngle(double x, double y)
    {
        var angle = Math.Atan2(y, x) * 180d / Math.PI;
        return Math.Abs(angle) <= 0.000001d ? 0d : angle;
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
