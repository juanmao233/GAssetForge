using System.Globalization;
using System.Text.Json.Nodes;
using GAssetForge.Core.IconDesign;

namespace GAssetForge.Core.Generation;

public sealed record IconDesignGenerationConstraintInput
{
    public IconDesignMode Mode { get; init; } = IconDesignMode.SpecifiedAngle;

    public int ImageWidth { get; init; } = 512;

    public int ImageHeight { get; init; } = 512;

    public double CameraAzimuthDegrees { get; init; } = 45d;

    public double CameraElevationDegrees { get; init; } = 30d;

    public double CameraRollDegrees { get; init; }

    public double CameraDistance { get; init; } = 4.5d;

    public double FocalLengthMillimeters { get; init; } = 50d;

    public double SensorHeightMillimeters { get; init; } = 24d;

    public double SubjectSize { get; init; } = 1d;

    public string ContentPrompt { get; init; } = "单个游戏美术 Icon";

    public string? StylePrompt { get; init; }

    public string? NegativePrompt { get; init; }

    public int OutputCount { get; init; } = 1;

    public GenerationBackgroundRemovalSpec? BackgroundRemoval { get; init; }
}

public sealed record IconDesignGenerationMetrics
{
    public string ModeKey { get; init; } = string.Empty;

    public string ImageAspectRatio { get; init; } = string.Empty;

    public IconVector3 CameraPosition { get; init; }

    public IconVector3 CameraForward { get; init; }

    public IconVector3 CameraRight { get; init; }

    public IconVector3 CameraUp { get; init; }

    public double VerticalFieldOfViewDegrees { get; init; }

    public double HorizontalFieldOfViewDegrees { get; init; }

    public IconProjectionBounds NormalizedBounds { get; init; }

    public bool IsFullyInFrame { get; init; }

    public string GenerationAngleDescription { get; init; } = string.Empty;
}

public static class IconDesignConstraintPackageBuilder
{
    public const string WorkflowId = "icon-design";
    public const string PreferredSkill = "imagegen";
    public const string SpecifiedAngleModeKey = "specified-angle";
    public const string ProjectionConvention = "right-handed-y-up-camera-orbits-origin-pinhole-v1";
    public const string TransparentBackgroundRule =
        "Icon 主体必须完整落在画幅内；主体外 alpha 必须为 0。透明背景必须是真实 alpha 通道，不得使用白底、纯色底或棋盘格模拟透明。";

    private static readonly IIconPerspectiveProjectionService ProjectionService =
        new IconPerspectiveProjectionService();

    public static GenerationConstraintPackage Build(IconDesignGenerationConstraintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);

        var projection = CalculateProjection(input);
        var metrics = CreateMetrics(projection);
        var artSize = $"{projection.Parameters.ImageWidth} × {projection.Parameters.ImageHeight} px";
        var backgroundInstruction = GenerationBackgroundContractBuilder.BuildInstruction(
            input.BackgroundRemoval,
            TransparentBackgroundRule,
            "Icon 主体轮廓");
        var outputCountInstruction = input.OutputCount == 1
            ? $"输出必须为单张 {artSize} PNG，保持精确宽高比 {metrics.ImageAspectRatio}。"
            : $"输出必须为 {input.OutputCount} 张 {artSize} PNG；每张都保持精确宽高比 {metrics.ImageAspectRatio}，" +
              "并作为同一物品描述下可辨识的 Icon 变体，不得复制同一图片冒充多个产物。";

        var iconParameters = new JsonObject
        {
            ["modeKey"] = metrics.ModeKey,
            ["projectionConvention"] = ProjectionConvention,
            ["imageWidth"] = projection.Parameters.ImageWidth,
            ["imageHeight"] = projection.Parameters.ImageHeight,
            ["imageAspectRatio"] = metrics.ImageAspectRatio,
            ["cameraAzimuthDegrees"] = projection.Parameters.CameraAzimuthDegrees,
            ["cameraElevationDegrees"] = projection.Parameters.CameraElevationDegrees,
            ["cameraRollDegrees"] = projection.Parameters.CameraRollDegrees,
            ["cameraDistance"] = projection.Parameters.CameraDistance,
            ["focalLengthMillimeters"] = projection.Parameters.FocalLengthMillimeters,
            ["sensorHeightMillimeters"] = projection.Parameters.SensorHeightMillimeters,
            ["subjectSize"] = projection.Parameters.SubjectSize,
            ["cameraPosition"] = BuildVectorNode(metrics.CameraPosition),
            ["cameraForward"] = BuildVectorNode(metrics.CameraForward),
            ["cameraRight"] = BuildVectorNode(metrics.CameraRight),
            ["cameraUp"] = BuildVectorNode(metrics.CameraUp),
            ["verticalFieldOfViewDegrees"] = metrics.VerticalFieldOfViewDegrees,
            ["horizontalFieldOfViewDegrees"] = metrics.HorizontalFieldOfViewDegrees,
            ["normalizedBounds"] = new JsonObject
            {
                ["minimumX"] = metrics.NormalizedBounds.MinimumX,
                ["minimumY"] = metrics.NormalizedBounds.MinimumY,
                ["maximumX"] = metrics.NormalizedBounds.MaximumX,
                ["maximumY"] = metrics.NormalizedBounds.MaximumY,
                ["widthPercentOfFrame"] = metrics.NormalizedBounds.WidthPercentOfFrame,
                ["heightPercentOfFrame"] = metrics.NormalizedBounds.HeightPercentOfFrame
            },
            ["fullyInFrame"] = metrics.IsFullyInFrame,
            ["generationAngleDescription"] = metrics.GenerationAngleDescription
        };

        var package = new GenerationConstraintPackage
        {
            WorkflowId = WorkflowId,
            PreferredSkill = PreferredSkill,
            Prompt = BuildPrompt(input, projection, metrics, artSize, backgroundInstruction),
            NegativePrompt = input.NegativePrompt,
            Output = new GenerationOutputSpec
            {
                Width = projection.Parameters.ImageWidth,
                Height = projection.Parameters.ImageHeight,
                Count = input.OutputCount,
                Format = "png",
                TransparentBackground = true,
                BackgroundRemoval = input.BackgroundRemoval
            },
            Rules =
            [
                new GenerationConstraintRule
                {
                    Key = "icon.canvas.exact-size",
                    Category = "output",
                    Instruction = outputCountInstruction
                },
                new GenerationConstraintRule
                {
                    Key = "icon.camera.orientation",
                    Category = "perspective",
                    Instruction =
                        $"使用 {metrics.ModeKey} 模式。相机方位角 {Format(projection.Parameters.CameraAzimuthDegrees)}°，" +
                        $"仰角 {Format(projection.Parameters.CameraElevationDegrees)}°，镜头画面顺时针滚转 " +
                        $"{Format(projection.Parameters.CameraRollDegrees)}°。方位角 0° 位于物体正前方 +Z，正值向 +X 环绕；" +
                        "仰角正值表示相机位于物体上方并向下观察。禁止改成等距、正投影或其它角度。"
                },
                new GenerationConstraintRule
                {
                    Key = "icon.camera.lens",
                    Category = "perspective",
                    Instruction =
                        $"使用 {Format(projection.Parameters.FocalLengthMillimeters)} mm 焦距、" +
                        $"{Format(projection.Parameters.SensorHeightMillimeters)} mm 传感器高度和 " +
                        $"{Format(projection.Parameters.CameraDistance)} 归一化物距；垂直 FOV " +
                        $"{Format(metrics.VerticalFieldOfViewDegrees)}°，水平 FOV " +
                        $"{Format(metrics.HorizontalFieldOfViewDegrees)}°。不得用风格参考覆盖镜头透视。"
                },
                new GenerationConstraintRule
                {
                    Key = "icon.subject.framing",
                    Category = "composition",
                    Instruction =
                        $"主体尺寸按归一化边长 {Format(projection.Parameters.SubjectSize)} 计算，完整居中落在画幅内；" +
                        $"当前投影占画幅约 {Format(metrics.NormalizedBounds.WidthPercentOfFrame)}% × " +
                        $"{Format(metrics.NormalizedBounds.HeightPercentOfFrame)}%。不得裁切主体、改变观察方向或添加 Tilemap 网格、卡牌边框和 UI 容器。"
                },
                new GenerationConstraintRule
                {
                    Key = "icon.background.transparent",
                    Category = "alpha",
                    Instruction = backgroundInstruction
                }
            ],
            Parameters = new JsonObject
            {
                ["iconDesign"] = iconParameters,
                ["background"] = GenerationBackgroundContractBuilder.BuildParameters(
                    input.BackgroundRemoval,
                    "outsideSubjectAlpha")
            }
        };

        GenerationInputValidator.ValidateConstraintPackage(package);
        return package;
    }

    public static IconProjectionSnapshot CalculateProjection(IconDesignGenerationConstraintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateGeometry(input);
        return ProjectionService.Calculate(new IconProjectionParameters
        {
            Mode = input.Mode,
            ImageWidth = input.ImageWidth,
            ImageHeight = input.ImageHeight,
            CameraAzimuthDegrees = input.CameraAzimuthDegrees,
            CameraElevationDegrees = input.CameraElevationDegrees,
            CameraRollDegrees = input.CameraRollDegrees,
            CameraDistance = input.CameraDistance,
            FocalLengthMillimeters = input.FocalLengthMillimeters,
            SensorHeightMillimeters = input.SensorHeightMillimeters,
            SubjectSize = input.SubjectSize
        });
    }

    public static IconDesignGenerationMetrics CalculateMetrics(IconDesignGenerationConstraintInput input) =>
        CreateMetrics(CalculateProjection(input));

    public static string BuildGenerationAngleDescription(IconProjectionSnapshot projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var parameters = projection.Parameters;
        return
            $"Icon 输出画幅为 {parameters.ImageWidth} × {parameters.ImageHeight} px。" +
            $"使用指定角度透视：相机围绕物体中心的方位角为 {Format(parameters.CameraAzimuthDegrees)}°" +
            "（0° 位于物体正前方 +Z，正值向物体右侧 +X 环绕），" +
            $"相机仰角为 {Format(parameters.CameraElevationDegrees)}°，镜头画面顺时针滚转 " +
            $"{Format(parameters.CameraRollDegrees)}°。使用 {Format(parameters.FocalLengthMillimeters)} mm 镜头，" +
            $"归一化物距 {Format(parameters.CameraDistance)}，垂直 FOV " +
            $"{Format(projection.VerticalFieldOfViewDegrees)}°、水平 FOV " +
            $"{Format(projection.HorizontalFieldOfViewDegrees)}°。主体必须遵循该相机透视并完整落在画幅内，" +
            "不得自行改为等距、正投影或其他角度。";
    }

    private static string BuildPrompt(
        IconDesignGenerationConstraintInput input,
        IconProjectionSnapshot projection,
        IconDesignGenerationMetrics metrics,
        string artSize,
        string backgroundInstruction)
    {
        var style = input.StylePrompt is null ? string.Empty : $" 美术风格：{input.StylePrompt}。";
        var subject = input.OutputCount == 1
            ? "生成单个、独立的游戏美术 Icon 资产。"
            : $"生成 {input.OutputCount} 个同类但可辨识的游戏美术 Icon 变体，不得复制同一图片冒充多个产物。";
        return $"{input.ContentPrompt}。{style}" +
               subject +
               $"每个输出画布必须为 {artSize}（宽高比 {metrics.ImageAspectRatio}）。" +
               BuildGenerationAngleDescription(projection) +
               $"当前相机位置为 ({Format(metrics.CameraPosition.X)}, {Format(metrics.CameraPosition.Y)}, {Format(metrics.CameraPosition.Z)})，" +
               $"主体投影占画幅约 {Format(metrics.NormalizedBounds.WidthPercentOfFrame)}% × " +
               $"{Format(metrics.NormalizedBounds.HeightPercentOfFrame)}%。" +
               "只生成描述物品本身，不得添加 Tilemap 地面网格、场景拼块轮廓、卡牌外框、UI 容器、文字或水印。" +
               backgroundInstruction;
    }

    private static IconDesignGenerationMetrics CreateMetrics(IconProjectionSnapshot projection)
    {
        var width = projection.Parameters.ImageWidth;
        var height = projection.Parameters.ImageHeight;
        var divisor = GreatestCommonDivisor(width, height);
        return new IconDesignGenerationMetrics
        {
            ModeKey = projection.Parameters.Mode switch
            {
                IconDesignMode.SpecifiedAngle => SpecifiedAngleModeKey,
                _ => throw new ArgumentOutOfRangeException(nameof(projection), "Icon 制作模式不受支持。")
            },
            ImageAspectRatio = $"{width / divisor}:{height / divisor}",
            CameraPosition = projection.CameraPosition,
            CameraForward = projection.CameraForward,
            CameraRight = projection.CameraRight,
            CameraUp = projection.CameraUp,
            VerticalFieldOfViewDegrees = projection.VerticalFieldOfViewDegrees,
            HorizontalFieldOfViewDegrees = projection.HorizontalFieldOfViewDegrees,
            NormalizedBounds = projection.NormalizedBounds,
            IsFullyInFrame = projection.IsFullyInFrame,
            GenerationAngleDescription = BuildGenerationAngleDescription(projection)
        };
    }

    private static JsonObject BuildVectorNode(IconVector3 vector) => new()
    {
        ["x"] = vector.X,
        ["y"] = vector.Y,
        ["z"] = vector.Z
    };

    private static void Validate(IconDesignGenerationConstraintInput input)
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

    private static void ValidateGeometry(IconDesignGenerationConstraintInput input)
    {
        if (input.Mode != IconDesignMode.SpecifiedAngle)
        {
            throw new ArgumentOutOfRangeException(nameof(input.Mode), "当前仅支持指定角度生成模式。");
        }

        if (input.ImageWidth is < IconPerspectiveProjectionService.MinimumImageDimension or
            > IconPerspectiveProjectionService.MaximumImageDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(input.ImageWidth), "Icon 画幅宽度必须位于 16 到 8192 像素之间。");
        }

        if (input.ImageHeight is < IconPerspectiveProjectionService.MinimumImageDimension or
            > IconPerspectiveProjectionService.MaximumImageDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(input.ImageHeight), "Icon 画幅高度必须位于 16 到 8192 像素之间。");
        }

        ValidateFiniteRange(input.CameraAzimuthDegrees, -180d, 180d, nameof(input.CameraAzimuthDegrees));
        ValidateFiniteRange(
            input.CameraElevationDegrees,
            IconPerspectiveProjectionService.MinimumElevationDegrees,
            IconPerspectiveProjectionService.MaximumElevationDegrees,
            nameof(input.CameraElevationDegrees));
        ValidateFiniteRange(input.CameraRollDegrees, -180d, 180d, nameof(input.CameraRollDegrees));
        ValidateFiniteRange(
            input.CameraDistance,
            IconPerspectiveProjectionService.MinimumCameraDistance,
            IconPerspectiveProjectionService.MaximumCameraDistance,
            nameof(input.CameraDistance));
        ValidateFiniteRange(
            input.FocalLengthMillimeters,
            IconPerspectiveProjectionService.MinimumFocalLengthMillimeters,
            IconPerspectiveProjectionService.MaximumFocalLengthMillimeters,
            nameof(input.FocalLengthMillimeters));
        ValidateFiniteRange(input.SensorHeightMillimeters, 4d, 100d, nameof(input.SensorHeightMillimeters));
        ValidateFiniteRange(
            input.SubjectSize,
            IconPerspectiveProjectionService.MinimumSubjectSize,
            IconPerspectiveProjectionService.MaximumSubjectSize,
            nameof(input.SubjectSize));
    }

    private static void ValidateFiniteRange(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"{name} 必须位于 {Format(minimum)} 到 {Format(maximum)} 之间。");
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

    private static string Format(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);
}
