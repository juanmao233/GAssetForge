using System.Text.Json.Nodes;
using GAssetForge.Core.Generation;
using GAssetForge.Core.IconDesign;

namespace GAssetForge.SmokeTests;

internal static class IconDesignGenerationConstraintSmokeTests
{
    public static void Run()
    {
        BuilderFreezesCameraCanvasAndProjectionMetrics();
        BackgroundRemovalContractUsesSubjectSemantics();
        InvalidGenerationInputsFailFast();
    }

    private static void BuilderFreezesCameraCanvasAndProjectionMetrics()
    {
        var input = new IconDesignGenerationConstraintInput
        {
            ImageWidth = 640,
            ImageHeight = 512,
            CameraAzimuthDegrees = -35d,
            CameraElevationDegrees = 22d,
            CameraRollDegrees = 8d,
            CameraDistance = 6.25d,
            FocalLengthMillimeters = 70d,
            ContentPrompt = "单个镶嵌蓝色宝石的银质护符",
            StylePrompt = "低饱和手绘奇幻游戏图标，清晰外轮廓",
            NegativePrompt = "文字、水印、卡牌边框",
            OutputCount = 2
        };

        var package = IconDesignConstraintPackageBuilder.Build(input);
        var metrics = IconDesignConstraintPackageBuilder.CalculateMetrics(input);
        var icon = package.Parameters["iconDesign"] as JsonObject
            ?? throw new InvalidOperationException("Icon 约束缺少 parameters.iconDesign。");

        Equal(IconDesignConstraintPackageBuilder.WorkflowId, package.WorkflowId, "Icon workflowId");
        Equal(IconDesignConstraintPackageBuilder.PreferredSkill, package.PreferredSkill, "Icon preferredSkill");
        Equal(640, package.Output.Width, "Icon 输出宽度");
        Equal(512, package.Output.Height, "Icon 输出高度");
        Equal(2, package.Output.Count, "Icon 输出数量");
        True(package.Output.TransparentBackground, "Icon 最终产物必须声明真实透明背景");
        Equal(IconDesignConstraintPackageBuilder.SpecifiedAngleModeKey,
            ReadString(icon, "modeKey"),
            "Icon 制作模式键");
        Equal(IconDesignConstraintPackageBuilder.ProjectionConvention,
            ReadString(icon, "projectionConvention"),
            "Icon 投影坐标约定");
        Equal("5:4", ReadString(icon, "imageAspectRatio"), "Icon 画幅比例");
        Near(input.CameraAzimuthDegrees, ReadDouble(icon, "cameraAzimuthDegrees"), 0.000000001d,
            "Icon 方位角冻结");
        Near(input.CameraElevationDegrees, ReadDouble(icon, "cameraElevationDegrees"), 0.000000001d,
            "Icon 仰角冻结");
        Near(input.CameraRollDegrees, ReadDouble(icon, "cameraRollDegrees"), 0.000000001d,
            "Icon 滚转角冻结");
        Near(input.CameraDistance, ReadDouble(icon, "cameraDistance"), 0.000000001d,
            "Icon 物距冻结");
        Near(input.FocalLengthMillimeters, ReadDouble(icon, "focalLengthMillimeters"), 0.000000001d,
            "Icon 焦距冻结");
        Near(metrics.VerticalFieldOfViewDegrees,
            ReadDouble(icon, "verticalFieldOfViewDegrees"),
            0.000000001d,
            "Icon 垂直 FOV 应由统一投影服务计算");
        Near(metrics.HorizontalFieldOfViewDegrees,
            ReadDouble(icon, "horizontalFieldOfViewDegrees"),
            0.000000001d,
            "Icon 水平 FOV 应由统一投影服务计算");
        Equal(metrics.GenerationAngleDescription,
            ReadString(icon, "generationAngleDescription"),
            "UI 与生成任务应共享同一角度描述格式器");
        True(package.Prompt.Contains("方位角为 -35°", StringComparison.Ordinal) &&
             package.Prompt.Contains("70 mm", StringComparison.Ordinal) &&
             package.Prompt.Contains("不得自行改为等距、正投影或其他角度", StringComparison.Ordinal),
            "Icon 提示必须实际注入相机角度、镜头与禁止改角规则");
        True(package.Rules.Any(rule => rule.Key == "icon.camera.orientation") &&
             package.Rules.Any(rule => rule.Key == "icon.camera.lens") &&
             package.Rules.Any(rule => rule.Key == "icon.subject.framing"),
            "Icon 约束必须包含方向、镜头与构图规则");

        GenerationInputValidator.ValidateConstraintPackage(package);
    }

    private static void BackgroundRemovalContractUsesSubjectSemantics()
    {
        var removal = new GenerationBackgroundRemovalSpec
        {
            Preset = "magenta",
            ColorHex = "#FF00FF"
        };
        var package = IconDesignConstraintPackageBuilder.Build(new IconDesignGenerationConstraintInput
        {
            ContentPrompt = "单个金属宝箱 Icon",
            BackgroundRemoval = removal
        });
        var background = package.Parameters["background"] as JsonObject
            ?? throw new InvalidOperationException("Icon 约束缺少 background 参数。");
        var generation = background["generationStage"] as JsonObject
            ?? throw new InvalidOperationException("Icon 约束缺少 generationStage。");
        var final = background["finalStage"] as JsonObject
            ?? throw new InvalidOperationException("Icon 约束缺少 finalStage。");

        Equal(TilemapConstraintPackageBuilder.SolidOpaqueColorBackgroundMode,
            ReadString(generation, "mode"),
            "Icon 首道工序应使用纯色底");
        Equal("#FF00FF", ReadString(generation, "colorHex"), "Icon 首道底色");
        Equal(TilemapConstraintPackageBuilder.TransparentAlphaBackgroundMode,
            ReadString(final, "mode"),
            "Icon 最终工序应为真实透明 Alpha");
        Equal(0, ReadInt(final, "outsideSubjectAlpha"), "Icon 主体外 Alpha");
        False(final.ContainsKey("outsideDiamondAlpha"),
            "Icon 背景合同不得泄漏 Tilemap 菱形语义");
        True(package.Prompt.Contains("Icon 主体轮廓外", StringComparison.Ordinal),
            "Icon 纯色底提示应使用主体轮廓语义");
    }

    private static void InvalidGenerationInputsFailFast()
    {
        Throws<ArgumentOutOfRangeException>(
            () => IconDesignConstraintPackageBuilder.Build(new IconDesignGenerationConstraintInput
            {
                CameraElevationDegrees = 90d,
                ContentPrompt = "非法仰角 Icon"
            }),
            "Icon 生成合同不得静默收束越界仰角");
        Throws<ArgumentOutOfRangeException>(
            () => IconDesignConstraintPackageBuilder.Build(new IconDesignGenerationConstraintInput
            {
                FocalLengthMillimeters = double.NaN,
                ContentPrompt = "非法焦距 Icon"
            }),
            "Icon 生成合同不得接受非有限焦距");
        Throws<ArgumentException>(
            () => IconDesignConstraintPackageBuilder.Build(new IconDesignGenerationConstraintInput
            {
                ContentPrompt = "  未规范化 Icon  "
            }),
            "Icon 生成内容必须先规范化再冻结");
    }

    private static string ReadString(JsonObject node, string name) =>
        node[name]?.GetValue<string>()
        ?? throw new InvalidOperationException($"缺少字符串字段 {name}。");

    private static double ReadDouble(JsonObject node, string name) =>
        node[name]?.GetValue<double>()
        ?? throw new InvalidOperationException($"缺少数值字段 {name}。");

    private static int ReadInt(JsonObject node, string name) =>
        node[name]?.GetValue<int>()
        ?? throw new InvalidOperationException($"缺少整数字段 {name}。");

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{message}：预期 {expected} ± {tolerance}，实际 {actual}。");
        }
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);
}
