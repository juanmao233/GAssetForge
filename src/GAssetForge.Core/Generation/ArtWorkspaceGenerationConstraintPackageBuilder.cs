using System.Globalization;
using System.Text.Json.Nodes;
using GAssetForge.Core.ArtWorkspaces;

namespace GAssetForge.Core.Generation;

public sealed record ArtWorkspaceGenerationReference
{
    public string ImageId { get; init; } = string.Empty;

    public string AssetId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public string MediaType { get; init; } = string.Empty;

    public string DataRootRelativePath { get; init; } = string.Empty;

    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public double RotationDegrees { get; init; }

    public double Opacity { get; init; } = 1d;

    public int ZIndex { get; init; }
}

public sealed record ArtWorkspaceGenerationRegion
{
    public double X { get; init; }

    public double Y { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }
}

public sealed record ArtWorkspaceGenerationInstructionReference
{
    public string ImageId { get; init; } = string.Empty;

    public ArtWorkspaceGenerationRegion? Region { get; init; }
}

public static class ArtWorkspaceGenerationInstructionSegmentKinds
{
    public const string Text = "text";

    public const string ImageReference = "image-reference";
}

/// <summary>
/// One immutable part of the user's ordered Art Workspace instruction. Image-reference segments
/// intentionally carry their own token identity and region snapshot, so the same canvas image may
/// be inserted more than once with different local selections without later editor selection
/// changes altering the submitted meaning.
/// </summary>
public sealed record ArtWorkspaceGenerationInstructionSegment
{
    public string Kind { get; init; } = ArtWorkspaceGenerationInstructionSegmentKinds.Text;

    public string Text { get; init; } = string.Empty;

    public string TokenId { get; init; } = string.Empty;

    public string ImageId { get; init; } = string.Empty;

    public ArtWorkspaceGenerationRegion? Region { get; init; }
}

public sealed record ArtWorkspaceGenerationConstraintInput
{
    public string RequestId { get; init; } = string.Empty;

    /// <summary>
    /// Backward-compatible plain-text summary used by legacy consumers and task-list display.
    /// When <see cref="InstructionSegments"/> is non-empty, those ordered segments are the
    /// authoritative generation instruction and this value must not be used to infer token order.
    /// </summary>
    public string Instruction { get; init; } = string.Empty;

    public string OutputFrameName { get; init; } = string.Empty;

    public double OutputFrameX { get; init; }

    public double OutputFrameY { get; init; }

    public double OutputFrameWidth { get; init; }

    public double OutputFrameHeight { get; init; }

    public int OutputPixelWidth { get; init; } = 1024;

    public int OutputPixelHeight { get; init; } = 1024;

    public string OutputBackgroundHex { get; init; } = "#20242C";

    public string OutputRelativeDirectory { get; init; } = string.Empty;

    public ArtWorkspaceGenerationReference[] VisibleReferences { get; init; } = [];

    /// <summary>
    /// Ordered text and image tokens that preserve the exact insertion point of every referenced
    /// image inside the instruction. An empty collection is treated as a legacy request and is
    /// reconstructed from <see cref="Instruction"/> and <see cref="InstructionReference"/>.
    /// </summary>
    public ArtWorkspaceGenerationInstructionSegment[] InstructionSegments { get; init; } = [];

    /// <summary>
    /// Identifies which frozen canvas image the user's deictic wording (for example “this image”
    /// or “this area”) refers to. The optional region is normalized in that image's unrotated
    /// local source space and is intentionally kept separate from the user's editable text.
    /// </summary>
    public ArtWorkspaceGenerationInstructionReference? InstructionReference { get; init; }
}

/// <summary>
/// Freezes an Art Workspace conversation request into the same provider-neutral generation
/// contract consumed by the managed GAssetForge Agent harness. Canvas references are explicitly
/// composition references rather than style-library assets, so downstream adapters can preserve
/// their transforms without reinterpreting them as a reusable art style.
/// </summary>
public static class ArtWorkspaceGenerationConstraintPackageBuilder
{
    public const string WorkflowId = "art-workspace";
    public const string PreferredSkill = "imagegen";
    public const int MaximumReferenceCount = MaterialGenerationConstraintComposer.MaximumReferenceCount;

    public const int ContractSchemaVersion = 3;

    public const int MaximumInstructionSegmentCount = 256;

    public const int MaximumInlineInstructionReferenceCount =
        ArtWorkspaceInstructionSegmentSemantics.MaximumImageReferenceCount;

    public static int GetInstructionSemanticLength(
        IEnumerable<ArtWorkspaceGenerationInstructionSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var textLength = 0;
        var imageReferenceCount = 0;
        foreach (var segment in segments)
        {
            ArgumentNullException.ThrowIfNull(segment);
            if (string.Equals(
                    segment.Kind,
                    ArtWorkspaceGenerationInstructionSegmentKinds.Text,
                    StringComparison.Ordinal))
            {
                textLength = checked(textLength + (segment.Text?.Length ?? 0));
            }
            else if (string.Equals(
                         segment.Kind,
                         ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
                         StringComparison.Ordinal))
            {
                imageReferenceCount++;
            }
        }

        return ArtWorkspaceInstructionSegmentSemantics.MeasureSemanticLength(
            textLength,
            imageReferenceCount);
    }

    public static string GetInstructionReferenceLabel(int zeroBasedIndex)
    {
        if (zeroBasedIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zeroBasedIndex));
        }

        var value = checked(zeroBasedIndex + 1);
        var characters = new Stack<char>();
        while (value > 0)
        {
            value--;
            characters.Push((char)('A' + value % 26));
            value /= 26;
        }

        return new string(characters.ToArray());
    }

    public static GenerationConstraintPackage Build(ArtWorkspaceGenerationConstraintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);

        var references = input.VisibleReferences
            .OrderBy(reference => reference.ZIndex)
            .ThenBy(reference => reference.ImageId, StringComparer.Ordinal)
            .ToArray();
        var referenceNodes = new JsonArray();
        foreach (var reference in references)
        {
            referenceNodes.Add(new JsonObject
            {
                ["imageId"] = reference.ImageId,
                ["assetId"] = reference.AssetId,
                ["name"] = reference.Name,
                ["sha256"] = reference.Sha256.ToLowerInvariant(),
                ["mediaType"] = reference.MediaType,
                ["dataRootRelativePath"] = reference.DataRootRelativePath,
                ["pixelWidth"] = reference.PixelWidth,
                ["pixelHeight"] = reference.PixelHeight,
                ["x"] = reference.X,
                ["y"] = reference.Y,
                ["width"] = reference.Width,
                ["height"] = reference.Height,
                ["rotationDegrees"] = reference.RotationDegrees,
                ["opacity"] = reference.Opacity,
                ["zIndex"] = reference.ZIndex
            });
        }

        var instructionReferenceNode = BuildInstructionReferenceNode(
            input.InstructionReference,
            references);
        var effectiveSegments = BuildEffectiveInstructionSegments(input);
        var hasExplicitInstructionSegments = input.InstructionSegments.Length > 0;
        var instructionTextProjection = string.Concat(effectiveSegments
            .Where(segment => string.Equals(
                segment.Kind,
                ArtWorkspaceGenerationInstructionSegmentKinds.Text,
                StringComparison.Ordinal))
            .Select(segment => segment.Text));
        var instructionSegmentNodes = BuildInstructionSegmentNodes(effectiveSegments, references);
        var instructionReferenceNodes = BuildInstructionReferenceNodes(effectiveSegments, references);

        var package = new GenerationConstraintPackage
        {
            WorkflowId = WorkflowId,
            PreferredSkill = PreferredSkill,
            Prompt = BuildPrompt(input, references),
            Output = new GenerationOutputSpec
            {
                Width = input.OutputPixelWidth,
                Height = input.OutputPixelHeight,
                Count = 1,
                MaxParallelism = 1,
                Format = "png",
                TransparentBackground = false
            },
            Rules =
            [
                new GenerationConstraintRule
                {
                    Key = "art-workspace.output.exact-size",
                    Category = "output",
                    Instruction =
                        $"最终 PNG 必须精确为 {input.OutputPixelWidth}×{input.OutputPixelHeight} 像素，" +
                        "只返回一张完整画面，不得输出拼图、联系表、文字说明或多个候选。"
                },
                new GenerationConstraintRule
                {
                    Key = "art-workspace.instruction.fidelity",
                    Category = "content",
                    Instruction =
                        "用户对话指令是本任务的主要内容合同；不得用参考图中的既有主体替换用户明确要求创建的对象。"
                },
                new GenerationConstraintRule
                {
                    Key = "art-workspace.references.composition",
                    Category = "reference",
                    Instruction = references.Length == 0
                        ? "本次没有可见画布参考图；只按用户指令和输出框生成。"
                        : $"parameters.artWorkspace.references 冻结了 {references.Length} 张当前可见画布图片。" +
                          "它们是构图、形状、空间关系和局部视觉参考，不是美术风格素材库；" +
                          "必须结合其位置、尺寸、旋转、透明度与层级理解，不得重新扫描实时工作区或擅自改变冻结关系。"
                },
                new GenerationConstraintRule
                {
                    Key = "art-workspace.instruction-reference.identity",
                    Category = "reference",
                    Instruction = BuildInstructionReferenceRule(
                        input.InstructionReference,
                        input.InstructionSegments,
                        references)
                },
                new GenerationConstraintRule
                {
                    Key = "art-workspace.instruction-segments.order",
                    Category = "content",
                    Instruction = BuildInstructionSegmentRule(effectiveSegments, references)
                },
                new GenerationConstraintRule
                {
                    Key = "art-workspace.output-frame.snapshot",
                    Category = "composition",
                    Instruction =
                        $"生成结果对应冻结输出框“{input.OutputFrameName}”，编辑器坐标 " +
                        $"({Format(input.OutputFrameX)}, {Format(input.OutputFrameY)})、显示尺寸 " +
                        $"{Format(input.OutputFrameWidth)}×{Format(input.OutputFrameHeight)}。" +
                        $"背景色 {input.OutputBackgroundHex} 仅用于描述编辑器输出框预览，不是抠图键色，也不得触发背景剔除。"
                }
            ],
            Parameters = new JsonObject
            {
                ["artWorkspace"] = new JsonObject
                {
                    ["schemaVersion"] = ContractSchemaVersion,
                    ["requestId"] = input.RequestId,
                    ["instruction"] = input.Instruction,
                    ["instructionSummary"] = input.Instruction,
                    ["instructionMode"] = hasExplicitInstructionSegments
                        ? "ordered-segments"
                        : "legacy-text",
                    ["instructionAuthority"] = hasExplicitInstructionSegments
                        ? "instructionSegments"
                        : input.InstructionReference is null
                            ? "instruction"
                            : "instruction+instructionReference",
                    ["instructionTextProjection"] = instructionTextProjection,
                    ["outputFrame"] = new JsonObject
                    {
                        ["name"] = input.OutputFrameName,
                        ["x"] = input.OutputFrameX,
                        ["y"] = input.OutputFrameY,
                        ["width"] = input.OutputFrameWidth,
                        ["height"] = input.OutputFrameHeight,
                        ["pixelWidth"] = input.OutputPixelWidth,
                        ["pixelHeight"] = input.OutputPixelHeight,
                        ["backgroundHex"] = input.OutputBackgroundHex,
                        ["outputRelativeDirectory"] = input.OutputRelativeDirectory
                    },
                    ["references"] = referenceNodes,
                    ["instructionSegments"] = instructionSegmentNodes,
                    ["instructionReferences"] = instructionReferenceNodes,
                    ["instructionReference"] = instructionReferenceNode
                }
            }
        };

        GenerationInputValidator.ValidateConstraintPackage(package);
        return package;
    }

    private static string BuildPrompt(
        ArtWorkspaceGenerationConstraintInput input,
        IReadOnlyCollection<ArtWorkspaceGenerationReference> references)
    {
        var referenceInstruction = references.Count == 0
            ? "当前画布没有可见参考图片。"
            : $"当前画布冻结了 {references.Count} 张可见构图参考图片；" +
              "结合 parameters.artWorkspace.references 中的图片路径、画布位置、显示尺寸、旋转、透明度和层级理解它们。";
        var effectiveSegments = BuildEffectiveInstructionSegments(input);
        var selectedReferenceInstruction = BuildInstructionReferencePrompt(
            input.InstructionReference,
            references);
        var orderedInstruction = RenderOrderedInstruction(effectiveSegments, references);
        var orderedReferenceInstruction = BuildInstructionSegmentPrompt(effectiveSegments, references);
        return
            $"根据以下美术工作区有序对话指令创建一张可直接用于游戏制作的栅格美术资源：{orderedInstruction}\n" +
            $"输出必须精确为 {input.OutputPixelWidth}×{input.OutputPixelHeight} PNG。" +
            referenceInstruction +
            orderedReferenceInstruction +
            selectedReferenceInstruction +
            "参考图片只约束构图、对象关系、形状或局部视觉信息，不代表统一风格库，也不得原样复制无关内容。" +
            "最终只生成用户要求的完整美术画面，不添加解释文字、界面调试标记、水印或额外候选。";
    }

    private static void Validate(ArtWorkspaceGenerationConstraintInput input)
    {
        ValidateCanonicalGuid(input.RequestId, nameof(input.RequestId));
        ValidateText(input.Instruction, nameof(input.Instruction), 1, 16_000);
        ValidateText(input.OutputFrameName, nameof(input.OutputFrameName), 1, 260);
        ValidateFinite(input.OutputFrameX, nameof(input.OutputFrameX), -100_000d, 100_000d);
        ValidateFinite(input.OutputFrameY, nameof(input.OutputFrameY), -100_000d, 100_000d);
        ValidateFinite(input.OutputFrameWidth, nameof(input.OutputFrameWidth), 16d, 16_384d);
        ValidateFinite(input.OutputFrameHeight, nameof(input.OutputFrameHeight), 16d, 16_384d);
        if (input.OutputPixelWidth is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(input.OutputPixelWidth), "输出像素宽度必须位于 1 到 16384 之间。");
        }

        if (input.OutputPixelHeight is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(input.OutputPixelHeight), "输出像素高度必须位于 1 到 16384 之间。");
        }

        ValidateColor(input.OutputBackgroundHex, nameof(input.OutputBackgroundHex));
        if (!string.IsNullOrWhiteSpace(input.OutputRelativeDirectory))
        {
            _ = GenerationInputValidator.NormalizeRelativeAssetPath(input.OutputRelativeDirectory);
        }

        var references = input.VisibleReferences
            ?? throw new ArgumentException("可见参考图集合不能为空。", nameof(input.VisibleReferences));
        if (references.Length > MaximumReferenceCount)
        {
            throw new ArgumentException($"单次美术工作区生成最多携带 {MaximumReferenceCount} 张参考图。", nameof(input.VisibleReferences));
        }

        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            ArgumentNullException.ThrowIfNull(reference);
            ValidateCanonicalGuid(reference.ImageId, nameof(reference.ImageId));
            ValidateCanonicalGuid(reference.AssetId, nameof(reference.AssetId));
            if (!imageIds.Add(reference.ImageId))
            {
                throw new ArgumentException($"可见参考图 ID 重复：{reference.ImageId}。", nameof(input.VisibleReferences));
            }

            ValidateText(reference.Name, nameof(reference.Name), 1, 260);
            ValidateSha256(reference.Sha256, nameof(reference.Sha256));
            if (string.IsNullOrWhiteSpace(reference.MediaType) ||
                !reference.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("参考图媒体类型必须是 image/*。", nameof(input.VisibleReferences));
            }

            var path = GenerationInputValidator.NormalizeRelativeAssetPath(reference.DataRootRelativePath);
            if (!paths.Add(path))
            {
                throw new ArgumentException($"参考图快照路径重复：{path}。", nameof(input.VisibleReferences));
            }

            if (reference.PixelWidth is < 1 or > 65_535 || reference.PixelHeight is < 1 or > 65_535)
            {
                throw new ArgumentOutOfRangeException(nameof(input.VisibleReferences), "参考图像素尺寸无效。");
            }

            ValidateFinite(reference.X, nameof(reference.X), -100_000d, 100_000d);
            ValidateFinite(reference.Y, nameof(reference.Y), -100_000d, 100_000d);
            ValidateFinite(reference.Width, nameof(reference.Width), 1d, 32_768d);
            ValidateFinite(reference.Height, nameof(reference.Height), 1d, 32_768d);
            ValidateFinite(reference.RotationDegrees, nameof(reference.RotationDegrees), -36_000d, 36_000d);
            ValidateFinite(reference.Opacity, nameof(reference.Opacity), 0d, 1d);
            if (reference.ZIndex is < 0 or > 1_000_000)
            {
                throw new ArgumentOutOfRangeException(nameof(input.VisibleReferences), "参考图层级无效。");
            }
        }
        ValidateInstructionSegments(input.InstructionSegments, references);
        if (input.InstructionSegments.Length > 0 && input.InstructionReference is not null)
        {
            throw new ArgumentException(
                "有序内联图片指令不能同时写入旧版单图片 InstructionReference 字段。",
                nameof(input));
        }

        ValidateInstructionReference(input.InstructionReference, references);
    }

    private static ArtWorkspaceGenerationInstructionSegment[] BuildEffectiveInstructionSegments(
        ArtWorkspaceGenerationConstraintInput input)
    {
        if (input.InstructionSegments is { Length: > 0 })
        {
            return input.InstructionSegments.ToArray();
        }

        // A legacy InstructionReference identifies the target of deictic wording in the whole
        // instruction, but it never recorded an insertion point. Keep it in the dedicated legacy
        // field instead of inventing an image token at the end of the sentence. This preserves the
        // old meaning while ensuring schema-v3 instructionSegments only claims ordering data that
        // was actually captured by the inline editor.
        return
        [
            new ArtWorkspaceGenerationInstructionSegment
            {
                Kind = ArtWorkspaceGenerationInstructionSegmentKinds.Text,
                Text = input.Instruction
            }
        ];
    }

    private static JsonArray BuildInstructionSegmentNodes(
        IReadOnlyList<ArtWorkspaceGenerationInstructionSegment> segments,
        IReadOnlyCollection<ArtWorkspaceGenerationReference> references)
    {
        var referenceLabels = BuildReferenceLabels(segments);
        var nodes = new JsonArray();
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (string.Equals(
                    segment.Kind,
                    ArtWorkspaceGenerationInstructionSegmentKinds.Text,
                    StringComparison.Ordinal))
            {
                nodes.Add(new JsonObject
                {
                    ["order"] = index,
                    ["kind"] = ArtWorkspaceGenerationInstructionSegmentKinds.Text,
                    ["text"] = segment.Text
                });
                continue;
            }

            var reference = references.Single(candidate =>
                string.Equals(candidate.ImageId, segment.ImageId, StringComparison.Ordinal));
            nodes.Add(new JsonObject
            {
                ["order"] = index,
                ["kind"] = ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
                ["tokenId"] = segment.TokenId,
                ["referenceLabel"] = referenceLabels[segment.TokenId],
                ["imageId"] = reference.ImageId,
                ["mode"] = segment.Region is null ? "whole-image" : "region"
            });
        }

        return nodes;
    }

    private static JsonArray BuildInstructionReferenceNodes(
        IReadOnlyList<ArtWorkspaceGenerationInstructionSegment> segments,
        IReadOnlyCollection<ArtWorkspaceGenerationReference> references)
    {
        var referenceLabels = BuildReferenceLabels(segments);
        var nodes = new JsonArray();
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (!string.Equals(
                    segment.Kind,
                    ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var reference = references.Single(candidate =>
                string.Equals(candidate.ImageId, segment.ImageId, StringComparison.Ordinal));
            nodes.Add(BuildResolvedInstructionReferenceNode(
                segment.TokenId,
                index,
                referenceLabels[segment.TokenId],
                segment.Region,
                reference));
        }

        return nodes;
    }

    private static JsonObject BuildResolvedInstructionReferenceNode(
        string tokenId,
        int order,
        string referenceLabel,
        ArtWorkspaceGenerationRegion? region,
        ArtWorkspaceGenerationReference reference)
    {
        var node = new JsonObject
        {
            ["tokenId"] = tokenId,
            ["order"] = order,
            ["referenceLabel"] = referenceLabel,
            ["imageId"] = reference.ImageId,
            ["assetId"] = reference.AssetId,
            ["name"] = reference.Name,
            ["sha256"] = reference.Sha256.ToLowerInvariant(),
            ["mediaType"] = reference.MediaType,
            ["snapshotPath"] = reference.DataRootRelativePath,
            ["dataRootRelativePath"] = reference.DataRootRelativePath,
            ["pixelWidth"] = reference.PixelWidth,
            ["pixelHeight"] = reference.PixelHeight,
            ["mode"] = region is null ? "whole-image" : "region",
            ["canvasTransform"] = new JsonObject
            {
                ["x"] = reference.X,
                ["y"] = reference.Y,
                ["width"] = reference.Width,
                ["height"] = reference.Height,
                ["rotationDegrees"] = reference.RotationDegrees,
                ["opacity"] = reference.Opacity,
                ["zIndex"] = reference.ZIndex
            },
            ["region"] = null,
            ["normalizedBounds"] = null,
            ["sourcePixelBounds"] = null,
            ["canvasPolygon"] = null
        };
        var persistedRegion = region is null
            ? new ArtWorkspaceImageRegionSelection
            {
                ImageId = reference.ImageId,
                X = 0d,
                Y = 0d,
                Width = 1d,
                Height = 1d
            }
            : ToPersistedRegion(reference.ImageId, region);
        var pixels = ArtWorkspaceImageRegionGeometry.GetSourcePixelBounds(
            reference.PixelWidth,
            reference.PixelHeight,
            persistedRegion);
        var corners = ArtWorkspaceImageRegionGeometry.GetWorldCorners(
            reference.ImageId,
            reference.X,
            reference.Y,
            reference.Width,
            reference.Height,
            reference.RotationDegrees,
            persistedRegion);
        node["sourcePixelBounds"] = new JsonObject
        {
            ["x"] = pixels.X,
            ["y"] = pixels.Y,
            ["width"] = pixels.Width,
            ["height"] = pixels.Height
        };
        node["canvasPolygon"] = new JsonArray(corners
            .Select(point => (JsonNode)new JsonObject
            {
                ["x"] = point.X,
                ["y"] = point.Y
            })
            .ToArray());
        if (region is not null)
        {
            node["region"] = new JsonObject
            {
                ["coordinateSpace"] = "image-local-normalized-unrotated",
                ["x"] = region.X,
                ["y"] = region.Y,
                ["width"] = region.Width,
                ["height"] = region.Height
            };
            node["normalizedBounds"] = new JsonObject
            {
                ["x"] = region.X,
                ["y"] = region.Y,
                ["width"] = region.Width,
                ["height"] = region.Height
            };
        }

        return node;
    }

    private static Dictionary<string, string> BuildReferenceLabels(
        IReadOnlyList<ArtWorkspaceGenerationInstructionSegment> segments)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var ordinal = 0;
        foreach (var segment in segments.Where(segment => string.Equals(
                     segment.Kind,
                     ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
                     StringComparison.Ordinal)))
        {
            labels.Add(segment.TokenId, GetInstructionReferenceLabel(ordinal++));
        }

        return labels;
    }

    private static JsonObject? BuildInstructionReferenceNode(
        ArtWorkspaceGenerationInstructionReference? instructionReference,
        IReadOnlyCollection<ArtWorkspaceGenerationReference> references)
    {
        if (instructionReference is null)
        {
            return null;
        }

        var reference = references.Single(candidate =>
            string.Equals(candidate.ImageId, instructionReference.ImageId, StringComparison.Ordinal));
        var node = new JsonObject
        {
            ["imageId"] = reference.ImageId,
            ["assetId"] = reference.AssetId,
            ["name"] = reference.Name,
            ["sha256"] = reference.Sha256.ToLowerInvariant(),
            ["mediaType"] = reference.MediaType,
            ["dataRootRelativePath"] = reference.DataRootRelativePath,
            ["pixelWidth"] = reference.PixelWidth,
            ["pixelHeight"] = reference.PixelHeight,
            ["mode"] = instructionReference.Region is null ? "whole-image" : "region",
            ["canvasTransform"] = new JsonObject
            {
                ["x"] = reference.X,
                ["y"] = reference.Y,
                ["width"] = reference.Width,
                ["height"] = reference.Height,
                ["rotationDegrees"] = reference.RotationDegrees,
                ["opacity"] = reference.Opacity,
                ["zIndex"] = reference.ZIndex
            },
            ["relation"] = "用户对话中的‘所选图片’、‘这张图’等指代明确绑定此参考图。"
        };
        if (instructionReference.Region is not { } region)
        {
            node["region"] = null;
            return node;
        }

        var persistedRegion = ToPersistedRegion(reference.ImageId, region);
        var pixels = ArtWorkspaceImageRegionGeometry.GetSourcePixelBounds(
            reference.PixelWidth,
            reference.PixelHeight,
            persistedRegion);
        var corners = ArtWorkspaceImageRegionGeometry.GetWorldCorners(
            reference.ImageId,
            reference.X,
            reference.Y,
            reference.Width,
            reference.Height,
            reference.RotationDegrees,
            persistedRegion);
        node["region"] = new JsonObject
        {
            ["coordinateSpace"] = "image-local-normalized-unrotated",
            ["x"] = region.X,
            ["y"] = region.Y,
            ["width"] = region.Width,
            ["height"] = region.Height,
            ["sourcePixelBounds"] = new JsonObject
            {
                ["x"] = pixels.X,
                ["y"] = pixels.Y,
                ["width"] = pixels.Width,
                ["height"] = pixels.Height
            },
            ["canvasPolygon"] = new JsonArray(corners
                .Select(point => (JsonNode)new JsonObject
                {
                    ["x"] = point.X,
                    ["y"] = point.Y
                })
                .ToArray()),
            ["relation"] = "用户对话中的‘所选区域’、‘这块区域’等指代明确绑定此局部；不要把整张图或其它参考图误当目标。"
        };
        return node;
    }

    private static string BuildInstructionReferenceRule(
        ArtWorkspaceGenerationInstructionReference? instructionReference,
        IReadOnlyCollection<ArtWorkspaceGenerationInstructionSegment> explicitSegments,
        IReadOnlyCollection<ArtWorkspaceGenerationReference> references)
    {
        if (explicitSegments.Count > 0)
        {
            if (explicitSegments.All(segment => !string.Equals(
                    segment.Kind,
                    ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
                    StringComparison.Ordinal)))
            {
                return "用户没有在有序指令中插入图片 token；不得自行把任一可见参考图解释为‘这张图片’。";
            }

            return "本请求使用 parameters.artWorkspace.instructionSegments 中的内联图片 token 明确指代；" +
                   "旧 instructionReference 字段仅供历史合同读取，不得覆盖、重排或合并内联 token。";
        }

        if (instructionReference is null)
        {
            return "用户提交时没有指定单一图片目标；不得自行把某张可见参考图解释为‘所选图片’。";
        }

        var reference = references.Single(candidate =>
            string.Equals(candidate.ImageId, instructionReference.ImageId, StringComparison.Ordinal));
        return instructionReference.Region is null
            ? $"用户提交时明确选择了图片“{reference.Name}”。对话中的‘所选图片’、‘这张图’、‘它’优先指向该图片，而不是其它可见参考图。"
            : $"用户提交时明确选择了图片“{reference.Name}”上的一个局部框选区域。" +
              "对话中的‘所选区域’、‘这块区域’、‘这里’优先指向 parameters.artWorkspace.instructionReference.region；" +
              "必须按冻结的源像素范围理解，不得扩展为整张图或其它参考图。";
    }

    private static string BuildInstructionSegmentRule(
        IReadOnlyList<ArtWorkspaceGenerationInstructionSegment> segments,
        IReadOnlyCollection<ArtWorkspaceGenerationReference> references)
    {
        var imageSegments = segments
            .Where(segment => string.Equals(
                segment.Kind,
                ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
                StringComparison.Ordinal))
            .ToArray();
        if (imageSegments.Length == 0)
        {
            return "有序指令只包含文字；不得自行插入或猜测图片指代。";
        }

        var labels = BuildReferenceLabels(segments);
        var mappings = imageSegments.Select(segment =>
        {
            var reference = references.Single(candidate =>
                string.Equals(candidate.ImageId, segment.ImageId, StringComparison.Ordinal));
            return $"{labels[segment.TokenId]}=“{CompactReferenceName(reference.Name)}”" +
                   (segment.Region is null ? "整图" : "的冻结局部区域");
        });
        return
            $"有序指令包含 {imageSegments.Length} 个独立图片 token（{string.Join("；", mappings)}）。" +
            "必须严格按 instructionSegments 的原始顺序和每个 token 两侧文字理解其局部职责；" +
            "即使多个 token 指向同一 imageId，也必须按 tokenId 保持为可拥有不同区域与用途的独立引用。" +
            "不得要求 Agent 重新猜测 A/B/C 分别是哪张图，也不得把某个 token 的局部区域扩展成整图。";
    }

    private static string RenderOrderedInstruction(
        IReadOnlyList<ArtWorkspaceGenerationInstructionSegment> segments,
        IReadOnlyCollection<ArtWorkspaceGenerationReference> references)
    {
        var labels = BuildReferenceLabels(segments);
        var builder = new System.Text.StringBuilder();
        foreach (var segment in segments)
        {
            if (string.Equals(
                    segment.Kind,
                    ArtWorkspaceGenerationInstructionSegmentKinds.Text,
                    StringComparison.Ordinal))
            {
                builder.Append(segment.Text);
                continue;
            }

            var reference = references.Single(candidate =>
                string.Equals(candidate.ImageId, segment.ImageId, StringComparison.Ordinal));
            builder.Append("【图片引用 ")
                .Append(labels[segment.TokenId])
                .Append("：“")
                .Append(CompactReferenceName(reference.Name))
                .Append(segment.Region is null ? "”（整图）】" : "”（局部框选）】");
        }

        return builder.ToString();
    }

    private static string BuildInstructionSegmentPrompt(
        IReadOnlyList<ArtWorkspaceGenerationInstructionSegment> segments,
        IReadOnlyCollection<ArtWorkspaceGenerationReference> references)
    {
        var imageSegments = segments
            .Where(segment => string.Equals(
                segment.Kind,
                ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
                StringComparison.Ordinal))
            .ToArray();
        if (imageSegments.Length == 0)
        {
            return string.Empty;
        }

        var labels = BuildReferenceLabels(segments);
        var mappings = imageSegments.Select(segment =>
        {
            var reference = references.Single(candidate =>
                string.Equals(candidate.ImageId, segment.ImageId, StringComparison.Ordinal));
            return $"{labels[segment.TokenId]} 明确绑定“{CompactReferenceName(reference.Name)}”" +
                   (segment.Region is null
                       ? "整张图片"
                       : "的冻结局部框选（按 sourcePixelBounds 使用）");
        });
        return
            $"图片 token 按对话出现顺序固定为：{string.Join("；", mappings)}。" +
            "每个 token 在句子中的插入位置决定它修饰的对象或部件；" +
            "完整机器可读映射位于 parameters.artWorkspace.instructionSegments 与 instructionReferences。";
    }

    private static string BuildInstructionReferencePrompt(
        ArtWorkspaceGenerationInstructionReference? instructionReference,
        IReadOnlyCollection<ArtWorkspaceGenerationReference> references)
    {
        if (instructionReference is null)
        {
            return string.Empty;
        }

        var reference = references.Single(candidate =>
            string.Equals(candidate.ImageId, instructionReference.ImageId, StringComparison.Ordinal));
        return instructionReference.Region is null
            ? $"用户提交时选中了参考图“{reference.Name}”；用户指令里的‘这张图/所选图片/它’明确指该图。"
            : $"用户提交时选中了参考图“{reference.Name}”并框选了其中一个局部；" +
              "用户指令里的‘这块区域/所选区域/这里’明确指 parameters.artWorkspace.instructionReference.region，" +
              "应优先使用该局部的内容、形状或视觉信息。";
    }

    private static void ValidateInstructionReference(
        ArtWorkspaceGenerationInstructionReference? instructionReference,
        IReadOnlyCollection<ArtWorkspaceGenerationReference> references)
    {
        if (instructionReference is null)
        {
            return;
        }

        ValidateCanonicalGuid(instructionReference.ImageId, nameof(instructionReference.ImageId));
        if (references.All(reference =>
                !string.Equals(reference.ImageId, instructionReference.ImageId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "指令所选图片必须属于本次冻结的可见参考图。",
                nameof(instructionReference));
        }

        if (instructionReference.Region is { } region)
        {
            try
            {
                ArtWorkspaceImageRegionGeometry.ValidateRegion(
                    ToPersistedRegion(instructionReference.ImageId, region),
                    instructionReference.ImageId);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"指令框选区域无效：{exception.Message}", nameof(instructionReference), exception);
            }
        }
    }

    private static void ValidateInstructionSegments(
        ArtWorkspaceGenerationInstructionSegment[]? segments,
        IReadOnlyCollection<ArtWorkspaceGenerationReference> references)
    {
        if (segments is null)
        {
            throw new ArgumentException("有序指令片段集合不能为空。", nameof(segments));
        }

        if (segments.Length == 0)
        {
            return;
        }

        if (segments.Length > MaximumInstructionSegmentCount)
        {
            throw new ArgumentException(
                $"单条指令最多包含 {MaximumInstructionSegmentCount} 个有序片段。",
                nameof(segments));
        }

        var tokenIds = new HashSet<string>(StringComparer.Ordinal);
        var imageReferenceCount = 0;
        var textLength = 0;
        var hasSemanticContent = false;
        foreach (var segment in segments)
        {
            ArgumentNullException.ThrowIfNull(segment);
            if (string.Equals(
                    segment.Kind,
                    ArtWorkspaceGenerationInstructionSegmentKinds.Text,
                    StringComparison.Ordinal))
            {
                if (segment.Text is null ||
                    segment.Text.Length > 16_000 ||
                    segment.Text.Any(character => character == '\0'))
                {
                    throw new ArgumentException("有序指令的文字片段为空、过长或含无效字符。", nameof(segments));
                }

                if (!string.IsNullOrEmpty(segment.TokenId) ||
                    !string.IsNullOrEmpty(segment.ImageId) ||
                    segment.Region is not null)
                {
                    throw new ArgumentException("文字片段不得携带图片 token、图片 ID 或局部区域。", nameof(segments));
                }

                textLength = checked(textLength + segment.Text.Length);
                hasSemanticContent |= !string.IsNullOrWhiteSpace(segment.Text);
                continue;
            }

            if (!string.Equals(
                    segment.Kind,
                    ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException($"未知的有序指令片段类型：{segment.Kind}。", nameof(segments));
            }

            if (!string.IsNullOrEmpty(segment.Text))
            {
                throw new ArgumentException("图片引用片段不得同时携带文字内容。", nameof(segments));
            }

            ValidateCanonicalGuid(segment.TokenId, nameof(segment.TokenId));
            ValidateCanonicalGuid(segment.ImageId, nameof(segment.ImageId));
            if (!tokenIds.Add(segment.TokenId))
            {
                throw new ArgumentException($"有序指令图片 token ID 重复：{segment.TokenId}。", nameof(segments));
            }

            if (references.All(reference =>
                    !string.Equals(reference.ImageId, segment.ImageId, StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    "有序指令中的图片 token 必须指向本次冻结且可见的参考图。",
                    nameof(segments));
            }

            if (segment.Region is { } region)
            {
                try
                {
                    ArtWorkspaceImageRegionGeometry.ValidateRegion(
                        ToPersistedRegion(segment.ImageId, region),
                        segment.ImageId);
                }
                catch (ArgumentException exception)
                {
                    throw new ArgumentException(
                        $"有序指令图片 token 的冻结区域无效：{exception.Message}",
                        nameof(segments),
                        exception);
                }
            }

            imageReferenceCount++;
            hasSemanticContent = true;
        }

        if (!hasSemanticContent)
        {
            throw new ArgumentException("有序指令不能只包含空白文字。", nameof(segments));
        }

        var semanticLength = ArtWorkspaceInstructionSegmentSemantics.MeasureSemanticLength(
            textLength,
            imageReferenceCount);
        if (semanticLength > ArtWorkspaceInstructionSegmentSemantics.MaximumSemanticLength)
        {
            throw new ArgumentException(
                $"有序指令的文字与图片 token 语义总长度不能超过 {ArtWorkspaceInstructionSegmentSemantics.MaximumSemanticLength}。",
                nameof(segments));
        }

        if (imageReferenceCount > MaximumInlineInstructionReferenceCount)
        {
            throw new ArgumentException(
                $"单条指令最多插入 {MaximumInlineInstructionReferenceCount} 个图片引用。",
                nameof(segments));
        }
    }

    private static ArtWorkspaceImageRegionSelection ToPersistedRegion(
        string imageId,
        ArtWorkspaceGenerationRegion region) => new()
    {
        ImageId = imageId,
        X = region.X,
        Y = region.Y,
        Width = region.Width,
        Height = region.Height
    };

    private static void ValidateCanonicalGuid(string value, string name)
    {
        if (!Guid.TryParseExact(value, "N", out _))
        {
            throw new ArgumentException($"{name} 必须是 32 位 GUID。", name);
        }
    }

    private static void ValidateText(string? value, string name, int minimumLength, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length < minimumLength ||
            value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(character => character == '\0'))
        {
            throw new ArgumentException($"{name} 为空、过长或包含无效字符。", name);
        }
    }

    private static void ValidateSha256(string? value, string name)
    {
        if (value is not { Length: 64 } || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("参考图 SHA-256 必须是 64 位十六进制字符串。", name);
        }
    }

    private static void ValidateColor(string? value, string name)
    {
        if (value is not { Length: 7 } || value[0] != '#' || value.Skip(1).Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("输出框背景色必须为 #RRGGBB。", name);
        }
    }

    private static void ValidateFinite(double value, string name, double minimum, double maximum)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"{name} 必须位于 {Format(minimum)} 到 {Format(maximum)} 之间。");
        }
    }

    private static string Format(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string CompactReferenceName(string value) =>
        value.Length <= 80 ? value : value[..80] + "…";
}
