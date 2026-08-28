using System.Text.Json.Nodes;
using GAssetForge.Core.Generation;

namespace GAssetForge.SmokeTests;

internal static class ArtWorkspaceGenerationConstraintSmokeTests
{
    public static void Run()
    {
        BuilderFreezesInstructionFrameAndOrderedReferences();
        InvalidInputsFailClosed();
    }

    private static void BuilderFreezesInstructionFrameAndOrderedReferences()
    {
        var requestId = Guid.NewGuid().ToString("N");
        var lower = CreateReference("底层参考", 2, 120d, 180d, 0.65d);
        var upper = CreateReference("顶层参考", 7, 640d, 320d, 1d);
        var package = ArtWorkspaceGenerationConstraintPackageBuilder.Build(
            new ArtWorkspaceGenerationConstraintInput
            {
                RequestId = requestId,
                Instruction = "创建一枚位于输出框中央的蓝色奇幻游戏徽章。",
                OutputFrameName = "徽章输出",
                OutputFrameX = 640d,
                OutputFrameY = 720d,
                OutputFrameWidth = 512d,
                OutputFrameHeight = 384d,
                OutputPixelWidth = 512,
                OutputPixelHeight = 384,
                OutputBackgroundHex = "#20242C",
                OutputRelativeDirectory = "Generated/ArtWorkspace",
                VisibleReferences = [upper, lower],
                InstructionReference = new ArtWorkspaceGenerationInstructionReference
                {
                    ImageId = upper.ImageId,
                    Region = new ArtWorkspaceGenerationRegion
                    {
                        X = 0.25d,
                        Y = 0.2d,
                        Width = 0.5d,
                        Height = 0.4d
                    }
                }
            });

        Equal(ArtWorkspaceGenerationConstraintPackageBuilder.WorkflowId, package.WorkflowId,
            "Art Workspace workflowId");
        Equal(ArtWorkspaceGenerationConstraintPackageBuilder.PreferredSkill, package.PreferredSkill,
            "Art Workspace preferredSkill");
        Equal(512, package.Output.Width, "Art Workspace 输出宽度");
        Equal(384, package.Output.Height, "Art Workspace 输出高度");
        Equal(1, package.Output.Count, "Art Workspace 必须是一图一任务");
        False(package.Output.TransparentBackground, "Art Workspace 默认生成完整不透明画面");
        True(package.Output.BackgroundRemoval is null,
            "Art Workspace 完整画面不得隐式注入背景剔除合同");
        True(package.Prompt.Contains("蓝色奇幻游戏徽章", StringComparison.Ordinal) &&
             package.Prompt.Contains("512×384", StringComparison.Ordinal),
            "生成提示应包含用户指令和冻结像素画幅");

        var workspace = package.Parameters["artWorkspace"] as JsonObject
            ?? throw new InvalidOperationException("Art Workspace 约束缺少 parameters.artWorkspace。");
        Equal(requestId, ReadString(workspace, "requestId"), "冻结请求 ID");
        Equal("创建一枚位于输出框中央的蓝色奇幻游戏徽章。",
            ReadString(workspace, "instruction"),
            "冻结用户指令");
        var frame = workspace["outputFrame"] as JsonObject
            ?? throw new InvalidOperationException("Art Workspace 约束缺少 outputFrame。");
        Equal(512, ReadInt(frame, "pixelWidth"), "冻结输出像素宽度");
        Equal(384, ReadInt(frame, "pixelHeight"), "冻结输出像素高度");
        Equal("Generated/ArtWorkspace", ReadString(frame, "outputRelativeDirectory"),
            "冻结输出相对目录");

        var references = workspace["references"] as JsonArray
            ?? throw new InvalidOperationException("Art Workspace 约束缺少 references。");
        Equal(2, references.Count, "冻结可见参考图数量");
        var first = references[0] as JsonObject
            ?? throw new InvalidOperationException("第一张 Art Workspace 参考图无效。");
        var second = references[1] as JsonObject
            ?? throw new InvalidOperationException("第二张 Art Workspace 参考图无效。");
        Equal(lower.ImageId, ReadString(first, "imageId"), "参考图应按 ZIndex 从低到高冻结");
        Equal(2, ReadInt(first, "zIndex"), "底层参考图层级");
        Equal(upper.ImageId, ReadString(second, "imageId"), "顶层参考图顺序");
        Equal(7, ReadInt(second, "zIndex"), "顶层参考图层级");
        Near(0.65d, ReadDouble(first, "opacity"), 0.000000001d, "参考图透明度冻结");
        Near(120d, ReadDouble(first, "x"), 0.000000001d, "参考图 X 冻结");
        Near(180d, ReadDouble(first, "rotationDegrees"), 0.000000001d,
            "参考图旋转冻结");

        var instructionReference = workspace["instructionReference"] as JsonObject
            ?? throw new InvalidOperationException("Art Workspace 约束缺少 instructionReference。");
        Equal(upper.ImageId, ReadString(instructionReference, "imageId"),
            "对话图片指代必须冻结到当前所选图");
        Equal(upper.AssetId, ReadString(instructionReference, "assetId"),
            "对话图片指代必须同时冻结素材身份");
        Equal(upper.Sha256, ReadString(instructionReference, "sha256"),
            "对话图片指代必须自包含冻结 SHA-256");
        Equal("region", ReadString(instructionReference, "mode"),
            "局部图片指代必须明确声明 region 模式");
        var region = instructionReference["region"] as JsonObject
            ?? throw new InvalidOperationException("Art Workspace 约束缺少局部框选区域。");
        Equal("image-local-normalized-unrotated", ReadString(region, "coordinateSpace"),
            "区域坐标空间必须稳定绑定未旋转图片本地空间");
        Near(0.25d, ReadDouble(region, "x"), 0.000000001d, "区域归一化 X");
        Near(0.4d, ReadDouble(region, "height"), 0.000000001d, "区域归一化高度");
        var sourcePixels = region["sourcePixelBounds"] as JsonObject
            ?? throw new InvalidOperationException("Art Workspace 区域缺少源像素范围。");
        Equal(128, ReadInt(sourcePixels, "x"), "区域源像素 X");
        Equal(102, ReadInt(sourcePixels, "y"), "区域源像素 Y 应向外取整");
        Equal(256, ReadInt(sourcePixels, "width"), "区域源像素宽度");
        Equal(206, ReadInt(sourcePixels, "height"), "区域源像素高度应覆盖完整归一化范围");
        var polygon = region["canvasPolygon"] as JsonArray
            ?? throw new InvalidOperationException("Art Workspace 区域缺少冻结画布多边形。");
        Equal(4, polygon.Count, "旋转图片局部区域必须冻结四个画布角点");
        True(package.Prompt.Contains("这块区域", StringComparison.Ordinal) &&
             package.Prompt.Contains(upper.Name, StringComparison.Ordinal),
            "生成提示应明确用户局部指代绑定到哪张图片");

        True(package.Rules.Any(rule => rule.Key == "art-workspace.output.exact-size") &&
             package.Rules.Any(rule => rule.Key == "art-workspace.references.composition") &&
             package.Rules.Any(rule => rule.Key == "art-workspace.instruction-reference.identity") &&
             package.Rules.Any(rule => rule.Key == "art-workspace.output-frame.snapshot"),
            "Art Workspace 应冻结尺寸、构图参考和输出框规则");
        GenerationInputValidator.ValidateConstraintPackage(package);
    }

    private static void InvalidInputsFailClosed()
    {
        Throws<ArgumentException>(
            () => ArtWorkspaceGenerationConstraintPackageBuilder.Build(
                new ArtWorkspaceGenerationConstraintInput
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Instruction = "  未规范化指令  ",
                    OutputFrameName = "输出框",
                    OutputFrameWidth = 512d,
                    OutputFrameHeight = 512d
                }),
            "Art Workspace 指令必须在冻结前完成规范化");
        Throws<ArgumentOutOfRangeException>(
            () => ArtWorkspaceGenerationConstraintPackageBuilder.Build(
                new ArtWorkspaceGenerationConstraintInput
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Instruction = "非法超大输出",
                    OutputFrameName = "输出框",
                    OutputFrameWidth = 512d,
                    OutputFrameHeight = 512d,
                    OutputPixelWidth = 16385
                }),
            "Art Workspace 不得接受 Harness 无法处理的输出尺寸");
        Throws<ArgumentException>(
            () => ArtWorkspaceGenerationConstraintPackageBuilder.Build(
                new ArtWorkspaceGenerationConstraintInput
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Instruction = "重复参考图",
                    OutputFrameName = "输出框",
                    OutputFrameWidth = 512d,
                    OutputFrameHeight = 512d,
                    VisibleReferences =
                    [
                        CreateReference("A", 0, 0d, 0d, 1d),
                        CreateReference("B", 1, 0d, 0d, 1d) with
                        {
                            ImageId = "11111111111111111111111111111111"
                        },
                        CreateReference("C", 2, 0d, 0d, 1d) with
                        {
                            ImageId = "11111111111111111111111111111111"
                        }
                    ]
                }),
            "Art Workspace 不得冻结重复参考图 ID");
        var onlyReference = CreateReference("唯一参考", 0, 0d, 0d, 1d);
        Throws<ArgumentException>(
            () => ArtWorkspaceGenerationConstraintPackageBuilder.Build(
                new ArtWorkspaceGenerationConstraintInput
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Instruction = "非法图片指代",
                    OutputFrameName = "输出框",
                    OutputFrameWidth = 512d,
                    OutputFrameHeight = 512d,
                    VisibleReferences = [onlyReference],
                    InstructionReference = new ArtWorkspaceGenerationInstructionReference
                    {
                        ImageId = Guid.NewGuid().ToString("N")
                    }
                }),
            "对话图片指代不得指向本次冻结参考图之外的对象");
        Throws<ArgumentException>(
            () => ArtWorkspaceGenerationConstraintPackageBuilder.Build(
                new ArtWorkspaceGenerationConstraintInput
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Instruction = "非法越界区域",
                    OutputFrameName = "输出框",
                    OutputFrameWidth = 512d,
                    OutputFrameHeight = 512d,
                    VisibleReferences = [onlyReference],
                    InstructionReference = new ArtWorkspaceGenerationInstructionReference
                    {
                        ImageId = onlyReference.ImageId,
                        Region = new ArtWorkspaceGenerationRegion
                        {
                            X = 0.8d,
                            Y = 0.2d,
                            Width = 0.4d,
                            Height = 0.4d
                        }
                    }
                }),
            "对话局部区域不得越出所选图片边界");
    }

    private static ArtWorkspaceGenerationReference CreateReference(
        string name,
        int zIndex,
        double x,
        double rotation,
        double opacity) => new()
    {
        ImageId = Guid.NewGuid().ToString("N"),
        AssetId = Guid.NewGuid().ToString("N"),
        Name = name,
        Sha256 = new string(zIndex % 2 == 0 ? 'a' : 'b', 64),
        MediaType = "image/png",
        DataRootRelativePath = $"Generation/ReferenceSnapshots/{Guid.NewGuid():N}/reference.png",
        PixelWidth = 512,
        PixelHeight = 512,
        X = x,
        Y = 240d,
        Width = 320d,
        Height = 280d,
        RotationDegrees = rotation,
        Opacity = opacity,
        ZIndex = zIndex
    };

    private static string ReadString(JsonObject node, string name) =>
        node[name]?.GetValue<string>()
        ?? throw new InvalidOperationException($"缺少字符串字段 {name}。");

    private static int ReadInt(JsonObject node, string name) =>
        node[name]?.GetValue<int>()
        ?? throw new InvalidOperationException($"缺少整数字段 {name}。");

    private static double ReadDouble(JsonObject node, string name) =>
        node[name]?.GetValue<double>()
        ?? throw new InvalidOperationException($"缺少数值字段 {name}。");

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
