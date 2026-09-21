using System.Text.Json.Nodes;
using GAssetForge.Core.ArtWorkspaces;
using GAssetForge.Core.Generation;

namespace GAssetForge.SmokeTests;

internal static class ArtWorkspaceGenerationConstraintSmokeTests
{
    public static void Run()
    {
        BuilderFreezesInstructionFrameAndOrderedReferences();
        OrderedInlineReferencesPreserveInsertionAndRegionSnapshots();
        OrderedSegmentsRemainAuthoritativeOverLegacySummary();
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
                OutputRelativeDirectory = "Final",
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
        Equal("legacy-text", ReadString(workspace, "instructionMode"),
            "旧请求应明确声明 legacy-text 模式");
        Equal("instruction+instructionReference", ReadString(workspace, "instructionAuthority"),
            "旧单图请求应明确由文字与旧图片指代共同解释");
        Equal("创建一枚位于输出框中央的蓝色奇幻游戏徽章。",
            ReadString(workspace, "instructionTextProjection"),
            "旧请求的文字投影应保持原始指令");
        var frame = workspace["outputFrame"] as JsonObject
            ?? throw new InvalidOperationException("Art Workspace 约束缺少 outputFrame。");
        Equal(512, ReadInt(frame, "pixelWidth"), "冻结输出像素宽度");
        Equal(384, ReadInt(frame, "pixelHeight"), "冻结输出像素高度");
        Equal("Final", ReadString(frame, "outputRelativeDirectory"),
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
        var legacySegments = workspace["instructionSegments"] as JsonArray
            ?? throw new InvalidOperationException("旧版 Art Workspace 请求缺少兼容 instructionSegments。");
        Equal(1, legacySegments.Count,
            "旧版单图片指代只能保留原始文字片段，不得伪造不存在的图片插入位置");
        Equal(
            ArtWorkspaceGenerationInstructionSegmentKinds.Text,
            ReadString((JsonObject)legacySegments[0]!, "kind"),
            "旧版兼容片段应保持为纯文字");
        var legacyInlineReferences = workspace["instructionReferences"] as JsonArray
            ?? throw new InvalidOperationException("旧版 Art Workspace 请求缺少兼容 instructionReferences。");
        Equal(0, legacyInlineReferences.Count,
            "旧版 InstructionReference 不得被重复合成为句尾内联图片 token");

        True(package.Rules.Any(rule => rule.Key == "art-workspace.output.exact-size") &&
             package.Rules.Any(rule => rule.Key == "art-workspace.references.composition") &&
             package.Rules.Any(rule => rule.Key == "art-workspace.instruction-reference.identity") &&
             package.Rules.Any(rule => rule.Key == "art-workspace.output-frame.snapshot"),
            "Art Workspace 应冻结尺寸、构图参考和输出框规则");
        GenerationInputValidator.ValidateConstraintPackage(package);
    }

    private static void OrderedSegmentsRemainAuthoritativeOverLegacySummary()
    {
        var reference = CreateReference("局部护甲参考", 1, 80d, 12d, 1d) with
        {
            PixelWidth = 800,
            PixelHeight = 600
        };
        var tokenId = Guid.NewGuid().ToString("N");
        var package = ArtWorkspaceGenerationConstraintPackageBuilder.Build(
            new ArtWorkspaceGenerationConstraintInput
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Instruction = "仅供旧任务列表显示的兼容摘要。",
                InstructionSegments =
                [
                    TextSegment("把这张"),
                    ImageSegment(tokenId, reference.ImageId, 0.2d, 0.25d, 0.5d, 0.4d),
                    TextSegment("图片局部的护甲结构应用到新角色，保持角色其它部分不变。")
                ],
                OutputFrameName = "局部编辑输出",
                OutputFrameWidth = 512d,
                OutputFrameHeight = 512d,
                VisibleReferences = [reference]
            });

        var workspace = package.Parameters["artWorkspace"] as JsonObject
            ?? throw new InvalidOperationException("局部编辑合同缺少 parameters.artWorkspace。");
        Equal("ordered-segments", ReadString(workspace, "instructionMode"),
            "存在显式 token 时必须声明 ordered-segments 模式");
        Equal("instructionSegments", ReadString(workspace, "instructionAuthority"),
            "有序片段必须是唯一权威指令来源");
        Equal("仅供旧任务列表显示的兼容摘要。", ReadString(workspace, "instructionSummary"),
            "旧 instruction 摘要仍应兼容保留");
        Equal("把这张图片局部的护甲结构应用到新角色，保持角色其它部分不变。",
            ReadString(workspace, "instructionTextProjection"),
            "文字投影应只按有序文字片段拼接，不伪造图片位置或职责");
        True(package.Prompt.Contains("【图片引用 A", StringComparison.Ordinal) &&
             package.Prompt.Contains("图片局部的护甲结构应用到新角色", StringComparison.Ordinal),
            "生成 Prompt 必须使用 token 的真实插入位置解释局部编辑目标");
        False(package.Prompt.Contains("仅供旧任务列表显示的兼容摘要", StringComparison.Ordinal),
            "显式有序片段存在时不得把兼容摘要重新注入 Prompt 形成双重语义");

        var inlineReferences = workspace["instructionReferences"] as JsonArray
            ?? throw new InvalidOperationException("局部编辑合同缺少 instructionReferences。");
        var resolved = inlineReferences.Single() as JsonObject
            ?? throw new InvalidOperationException("局部编辑引用解析无效。");
        Equal(tokenId, ReadString(resolved, "tokenId"), "局部编辑 token 身份");
        Equal("region", ReadString(resolved, "mode"), "局部编辑必须声明 region 模式");
        var sourceBounds = resolved["sourcePixelBounds"] as JsonObject
            ?? throw new InvalidOperationException("局部编辑引用缺少源像素范围。");
        Equal(160, ReadInt(sourceBounds, "x"), "局部编辑源像素 X");
        Equal(150, ReadInt(sourceBounds, "y"), "局部编辑源像素 Y");
        Equal(400, ReadInt(sourceBounds, "width"), "局部编辑源像素宽度");
        Equal(240, ReadInt(sourceBounds, "height"), "局部编辑源像素高度");
        var canvasPolygon = resolved["canvasPolygon"] as JsonArray
            ?? throw new InvalidOperationException("局部编辑引用缺少画布多边形。");
        Equal(4, canvasPolygon.Count, "局部编辑引用应冻结旋转后的四角范围");
        GenerationInputValidator.ValidateConstraintPackage(package);
    }

    private static void OrderedInlineReferencesPreserveInsertionAndRegionSnapshots()
    {
        var armor = CreateReference("护甲参考", 1, 80d, 0d, 1d) with
        {
            PixelWidth = 1000,
            PixelHeight = 800
        };
        var hair = CreateReference("发型参考", 2, 320d, 15d, 1d);
        var character = CreateReference("角色参考", 3, 640d, -10d, 0.9d);
        var armorToken = Guid.NewGuid().ToString("N");
        var hairToken = Guid.NewGuid().ToString("N");
        var characterToken = Guid.NewGuid().ToString("N");
        var repeatedArmorToken = Guid.NewGuid().ToString("N");
        var segments = new[]
        {
            TextSegment("我需要这张"),
            ImageSegment(armorToken, armor.ImageId, 0.1d, 0.2d, 0.3d, 0.25d),
            TextSegment("图片的护甲，加上这张"),
            ImageSegment(hairToken, hair.ImageId),
            TextSegment("图片的发型，并使用这张"),
            ImageSegment(characterToken, character.ImageId),
            TextSegment("图片的角色体型；护甲纹章再参考这张"),
            ImageSegment(repeatedArmorToken, armor.ImageId, 0.55d, 0.6d, 0.2d, 0.25d),
            TextSegment("图片中的局部。")
        };

        var package = ArtWorkspaceGenerationConstraintPackageBuilder.Build(
            new ArtWorkspaceGenerationConstraintInput
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Instruction = "我需要组合护甲、发型、角色体型和护甲纹章。",
                InstructionSegments = segments,
                OutputFrameName = "组合角色输出",
                OutputFrameWidth = 512d,
                OutputFrameHeight = 512d,
                OutputPixelWidth = 512,
                OutputPixelHeight = 512,
                VisibleReferences = [character, armor, hair]
            });

        var workspace = package.Parameters["artWorkspace"] as JsonObject
            ?? throw new InvalidOperationException("多图片指令合同缺少 parameters.artWorkspace。");
        Equal(ArtWorkspaceGenerationConstraintPackageBuilder.ContractSchemaVersion,
            ReadInt(workspace, "schemaVersion"),
            "多图片指令合同版本");
        var serializedSegments = workspace["instructionSegments"] as JsonArray
            ?? throw new InvalidOperationException("多图片指令合同缺少 instructionSegments。");
        Equal(segments.Length, serializedSegments.Count, "有序指令段数量");
        for (var index = 0; index < serializedSegments.Count; index++)
        {
            var node = serializedSegments[index] as JsonObject
                ?? throw new InvalidOperationException($"第 {index} 个指令段无效。");
            Equal(index, ReadInt(node, "order"), $"第 {index} 个指令段顺序");
            Equal(segments[index].Kind, ReadString(node, "kind"), $"第 {index} 个指令段类型");
        }

        Equal(armorToken,
            ReadString((JsonObject)serializedSegments[1]!, "tokenId"),
            "第一处护甲图片 token 身份");
        Equal("A",
            ReadString((JsonObject)serializedSegments[1]!, "referenceLabel"),
            "第一处图片应按插入顺序标为 A");
        Equal(hairToken,
            ReadString((JsonObject)serializedSegments[3]!, "tokenId"),
            "发型图片 token 身份");
        Equal("B",
            ReadString((JsonObject)serializedSegments[3]!, "referenceLabel"),
            "第二处图片应按插入顺序标为 B");
        Equal(characterToken,
            ReadString((JsonObject)serializedSegments[5]!, "tokenId"),
            "角色图片 token 身份");
        Equal("C",
            ReadString((JsonObject)serializedSegments[5]!, "referenceLabel"),
            "第三处图片应按插入顺序标为 C");
        Equal(repeatedArmorToken,
            ReadString((JsonObject)serializedSegments[7]!, "tokenId"),
            "同一护甲图片的第二处 token 必须保持独立身份");
        Equal("D",
            ReadString((JsonObject)serializedSegments[7]!, "referenceLabel"),
            "同图重复引用仍应获得独立的插入序号");
        Equal(armor.ImageId,
            ReadString((JsonObject)serializedSegments[7]!, "imageId"),
            "同图重复引用应保留相同图片身份");

        var inlineReferences = workspace["instructionReferences"] as JsonArray
            ?? throw new InvalidOperationException("多图片指令合同缺少 instructionReferences。");
        Equal(4, inlineReferences.Count, "每个图片 token 都必须解析为独立引用");
        var firstArmor = inlineReferences[0] as JsonObject
            ?? throw new InvalidOperationException("第一处护甲引用无效。");
        var secondArmor = inlineReferences[3] as JsonObject
            ?? throw new InvalidOperationException("第二处护甲引用无效。");
        Equal(armor.ImageId, ReadString(firstArmor, "imageId"), "第一处护甲引用图片");
        Equal(armor.ImageId, ReadString(secondArmor, "imageId"), "第二处护甲引用图片");
        False(string.Equals(
                ReadString(firstArmor, "tokenId"),
                ReadString(secondArmor, "tokenId"),
                StringComparison.Ordinal),
            "同一图片重复引用不得合并 token 身份");
        var firstArmorRegion = firstArmor["region"] as JsonObject
            ?? throw new InvalidOperationException("第一处护甲引用缺少独立局部区域。");
        var secondArmorRegion = secondArmor["region"] as JsonObject
            ?? throw new InvalidOperationException("第二处护甲引用缺少独立局部区域。");
        Near(0.1d, ReadDouble(firstArmorRegion, "x"), 0.000000001d,
            "第一处护甲局部 X");
        Near(0.55d, ReadDouble(secondArmorRegion, "x"), 0.000000001d,
            "第二处护甲局部 X");
        var firstPixels = firstArmor["sourcePixelBounds"] as JsonObject
            ?? throw new InvalidOperationException("第一处护甲引用缺少源像素范围。");
        var secondPixels = secondArmor["sourcePixelBounds"] as JsonObject
            ?? throw new InvalidOperationException("第二处护甲引用缺少源像素范围。");
        Equal(100, ReadInt(firstPixels, "x"), "第一处护甲源像素 X");
        Equal(550, ReadInt(secondPixels, "x"), "第二处护甲源像素 X");

        var promptReferenceIndexes = new[]
        {
            package.Prompt.IndexOf("【图片引用 A", StringComparison.Ordinal),
            package.Prompt.IndexOf("【图片引用 B", StringComparison.Ordinal),
            package.Prompt.IndexOf("【图片引用 C", StringComparison.Ordinal),
            package.Prompt.IndexOf("【图片引用 D", StringComparison.Ordinal)
        };
        True(promptReferenceIndexes.All(index => index >= 0) &&
             promptReferenceIndexes.SequenceEqual(promptReferenceIndexes.Order()),
            "生成 Prompt 必须按用户插入顺序解释 A/B/C/D 图片引用");
        True(package.Prompt.Contains(armor.Name, StringComparison.Ordinal) &&
             package.Prompt.Contains(hair.Name, StringComparison.Ordinal) &&
             package.Prompt.Contains(character.Name, StringComparison.Ordinal),
            "生成 Prompt 应将每个图片标签映射到可读图片名称");
        False(package.Prompt.Contains(armor.ImageId, StringComparison.Ordinal) ||
              package.Prompt.Contains(armor.AssetId, StringComparison.Ordinal) ||
              package.Prompt.Contains(armor.DataRootRelativePath, StringComparison.Ordinal),
            "生成 Prompt 不得把内部 ID 或冻结快照路径泄漏为自然语言指令");
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

        var duplicateToken = Guid.NewGuid().ToString("N");
        Throws<ArgumentException>(
            () => ArtWorkspaceGenerationConstraintPackageBuilder.Build(
                new ArtWorkspaceGenerationConstraintInput
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Instruction = "重复 token 必须拒绝",
                    InstructionSegments =
                    [
                        TextSegment("参考"),
                        ImageSegment(duplicateToken, onlyReference.ImageId),
                        TextSegment("以及"),
                        ImageSegment(duplicateToken, onlyReference.ImageId)
                    ],
                    OutputFrameName = "输出框",
                    OutputFrameWidth = 512d,
                    OutputFrameHeight = 512d,
                    VisibleReferences = [onlyReference]
                }),
            "多图片指令不得接受重复 token ID");
        Throws<ArgumentException>(
            () => ArtWorkspaceGenerationConstraintPackageBuilder.Build(
                new ArtWorkspaceGenerationConstraintInput
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Instruction = "不可见图片 token 必须拒绝",
                    InstructionSegments =
                    [
                        TextSegment("参考"),
                        ImageSegment(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"))
                    ],
                    OutputFrameName = "输出框",
                    OutputFrameWidth = 512d,
                    OutputFrameHeight = 512d,
                    VisibleReferences = [onlyReference]
                }),
            "图片 token 必须严格命中本次可见参考集合");
        Throws<ArgumentException>(
            () => ArtWorkspaceGenerationConstraintPackageBuilder.Build(
                new ArtWorkspaceGenerationConstraintInput
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Instruction = "越界 token 区域必须拒绝",
                    InstructionSegments =
                    [
                        TextSegment("参考"),
                        ImageSegment(
                            Guid.NewGuid().ToString("N"),
                            onlyReference.ImageId,
                            0.75d,
                            0.1d,
                            0.5d,
                            0.5d)
                    ],
                    OutputFrameName = "输出框",
                    OutputFrameWidth = 512d,
                    OutputFrameHeight = 512d,
                    VisibleReferences = [onlyReference]
                }),
            "每个图片 token 的局部区域都必须独立执行边界校验");
        Throws<ArgumentException>(
            () => ArtWorkspaceGenerationConstraintPackageBuilder.Build(
                new ArtWorkspaceGenerationConstraintInput
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Instruction = "新版与旧版字段不得混用",
                    InstructionSegments =
                    [
                        TextSegment("参考"),
                        ImageSegment(Guid.NewGuid().ToString("N"), onlyReference.ImageId)
                    ],
                    InstructionReference = new ArtWorkspaceGenerationInstructionReference
                    {
                        ImageId = onlyReference.ImageId
                    },
                    OutputFrameName = "输出框",
                    OutputFrameWidth = 512d,
                    OutputFrameHeight = 512d,
                    VisibleReferences = [onlyReference]
                }),
            "新版有序 token 与旧单图片字段同时出现时必须 fail-closed，避免职责覆盖");

        var exactBudgetSegments = new[]
        {
            TextSegment(new string(
                'a',
                ArtWorkspaceInstructionSegmentSemantics.MaximumSemanticLength -
                ArtWorkspaceInstructionSegmentSemantics.ImageReferenceSemanticLength)),
            ImageSegment(Guid.NewGuid().ToString("N"), onlyReference.ImageId)
        };
        Equal(
            ArtWorkspaceInstructionSegmentSemantics.MaximumSemanticLength,
            ArtWorkspaceGenerationConstraintPackageBuilder.GetInstructionSemanticLength(exactBudgetSegments),
            "合同层应把一个图片 token 的语义成本计入 16000 总预算");
        _ = ArtWorkspaceGenerationConstraintPackageBuilder.Build(
            new ArtWorkspaceGenerationConstraintInput
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Instruction = "恰好预算边界",
                InstructionSegments = exactBudgetSegments,
                OutputFrameName = "输出框",
                OutputFrameWidth = 512d,
                OutputFrameHeight = 512d,
                VisibleReferences = [onlyReference]
            });
        Throws<ArgumentException>(
            () => ArtWorkspaceGenerationConstraintPackageBuilder.Build(
                new ArtWorkspaceGenerationConstraintInput
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Instruction = "超过语义预算",
                    InstructionSegments =
                    [
                        TextSegment(new string(
                            'a',
                            ArtWorkspaceInstructionSegmentSemantics.MaximumSemanticLength -
                            ArtWorkspaceInstructionSegmentSemantics.ImageReferenceSemanticLength + 1)),
                        ImageSegment(Guid.NewGuid().ToString("N"), onlyReference.ImageId)
                    ],
                    OutputFrameName = "输出框",
                    OutputFrameWidth = 512d,
                    OutputFrameHeight = 512d,
                    VisibleReferences = [onlyReference]
                }),
            "合同层不得允许 15969 个可见字符加一个 token 绕过 16000 总预算");
    }

    private static ArtWorkspaceGenerationInstructionSegment TextSegment(string text) => new()
    {
        Kind = ArtWorkspaceGenerationInstructionSegmentKinds.Text,
        Text = text
    };

    private static ArtWorkspaceGenerationInstructionSegment ImageSegment(
        string tokenId,
        string imageId,
        double? x = null,
        double? y = null,
        double? width = null,
        double? height = null) => new()
    {
        Kind = ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
        TokenId = tokenId,
        ImageId = imageId,
        Region = x is null || y is null || width is null || height is null
            ? null
            : new ArtWorkspaceGenerationRegion
            {
                X = x.Value,
                Y = y.Value,
                Width = width.Value,
                Height = height.Value
            }
    };

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
