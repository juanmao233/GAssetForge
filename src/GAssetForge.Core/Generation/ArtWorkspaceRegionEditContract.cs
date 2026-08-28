using System.Text.Json.Nodes;

namespace GAssetForge.Core.Generation;

public sealed record ArtWorkspaceRegionEditPixelBounds
{
    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }
}

public sealed record ArtWorkspaceRegionEditTarget
{
    public string BaseImageId { get; init; } = string.Empty;

    public string BaseImageSha256 { get; init; } = string.Empty;

    public string BaseImageDataRootRelativePath { get; init; } = string.Empty;

    public int BasePixelWidth { get; init; }

    public int BasePixelHeight { get; init; }

    public ArtWorkspaceRegionEditPixelBounds SourcePixelBounds { get; init; } = new();
}

public sealed record ArtWorkspaceRegionEditContract
{
    public const int CurrentSchemaVersion = 1;
    public const string RegionEditOperationMode = "region-edit";
    public const string ExcludeHistoryPolicy = "exclude";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string OperationMode { get; init; } = RegionEditOperationMode;

    public string BaseImageId { get; init; } = string.Empty;

    public string BaseImageSha256 { get; init; } = string.Empty;

    /// <summary>
    /// Project Assets-relative immutable copy used as the authoritative edit base.
    /// </summary>
    public string BaseImagePath { get; init; } = string.Empty;

    /// <summary>
    /// Project Assets-relative PNG mask. Transparent pixels are editable by the Images Edit API;
    /// opaque pixels are preserved. The submission pipeline uses the same alpha distinction when
    /// performing the final deterministic composite.
    /// </summary>
    public string MaskPath { get; init; } = string.Empty;

    public string MaskSha256 { get; init; } = string.Empty;

    public string RegionPreviewPath { get; init; } = string.Empty;

    public string CompositePath { get; init; } = string.Empty;

    public string DifferenceReportPath { get; init; } = string.Empty;

    public string DifferencePreviewPath { get; init; } = string.Empty;

    public string HistoryReferencePolicy { get; init; } = ExcludeHistoryPolicy;

    public bool PreserveOutsideMask { get; init; } = true;

    public double MaximumOutsideMaskChangedPercent { get; init; } = 20d;

    public ArtWorkspaceRegionEditPixelBounds SourcePixelBounds { get; init; } = new();
}

/// <summary>
/// Resolves the user's single authoritative local-edit target and attaches the immutable pixel
/// contract used by the desktop publisher, MCP fallback, and output submission service.
/// </summary>
public static class ArtWorkspaceRegionEditContractComposer
{
    public const string RuleKey = "art-workspace.region-edit.pixel-lock";

    public static bool TryResolveRequestedTarget(
        GenerationConstraintPackage package,
        out ArtWorkspaceRegionEditTarget? target)
    {
        ArgumentNullException.ThrowIfNull(package);
        target = null;
        if (package.Parameters?["artWorkspace"] is not JsonObject workspace)
        {
            return false;
        }

        // Schema-v3 ordered instructions only become an in-place edit when the instruction has
        // exactly one image token and that token freezes a region. Multi-image instructions remain
        // compositional generation requests rather than being guessed into an inpaint operation.
        if (workspace["instructionReferences"] is JsonArray inlineReferences &&
            inlineReferences.Count > 0)
        {
            if (inlineReferences.Count != 1 || inlineReferences[0] is not JsonObject onlyReference ||
                !string.Equals(ReadOptionalString(onlyReference, "mode"), "region", StringComparison.Ordinal))
            {
                return false;
            }

            target = ReadTarget(onlyReference, sourceBoundsNestedUnderRegion: false);
            return true;
        }

        if (workspace["instructionReference"] is not JsonObject legacyReference ||
            !string.Equals(ReadOptionalString(legacyReference, "mode"), "region", StringComparison.Ordinal))
        {
            return false;
        }

        target = ReadTarget(legacyReference, sourceBoundsNestedUnderRegion: true);
        return true;
    }

    public static GenerationConstraintPackage Attach(
        GenerationConstraintPackage package,
        ArtWorkspaceRegionEditContract contract)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(contract);
        var safeContract = Normalize(contract);
        var safePackage = GenerationJson.Clone(package);
        if (safePackage.Parameters?["artWorkspace"] is not JsonObject workspace)
        {
            throw new ArgumentException(
                "局部编辑合同只能附加到 art-workspace 生成任务。",
                nameof(package));
        }

        workspace["editContract"] = BuildNode(safeContract);
        var bounds = safeContract.SourcePixelBounds;
        var expandedInstruction =
            "Region edit mode (pixel-locked):\n" +
            "Edit target: the authoritative current image identified by baseImageId=" +
            safeContract.BaseImageId + ".\n" +
            $"Allowed edit rectangle: x={bounds.X}, y={bounds.Y}, width={bounds.Width}, height={bounds.Height}.\n" +
            "Change only the requested subject inside this rectangle and the supplied mask. " +
            "Keep every unselected instance of the edited feature, all other objects, pose, " +
            "lighting, and every pixel outside the mask unchanged. Do not restore colors or " +
            "details from historical images.";
        var rules = safePackage.Rules
            .Where(rule => !string.Equals(rule.Key, RuleKey, StringComparison.OrdinalIgnoreCase))
            .Append(new GenerationConstraintRule
            {
                Key = RuleKey,
                Category = "region-edit",
                Required = true,
                Instruction = expandedInstruction.Replace('\n', ' ')
            })
            .ToArray();
        var result = safePackage with
        {
            Prompt = safePackage.Prompt.TrimEnd() + "\n\n" + expandedInstruction,
            Rules = rules
        };
        GenerationInputValidator.ValidateConstraintPackage(result);
        return result;
    }

    /// <summary>
    /// Rebinds only the attempt-local artifact paths while retaining the already-expanded prompt
    /// and rule text. Automatic retries must not append the region instruction repeatedly, and
    /// must never reuse a previous attempt's raw/composite/diagnostic files.
    /// </summary>
    public static GenerationConstraintPackage RebindAttemptArtifacts(
        GenerationConstraintPackage package,
        ArtWorkspaceRegionEditContract contract)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(contract);
        if (!HasDeclaredContract(package))
        {
            throw new ArgumentException(
                "只能为已经声明 region-edit 的任务重绑尝试工件。",
                nameof(package));
        }

        var safeContract = Normalize(contract);
        var safePackage = GenerationJson.Clone(package);
        if (safePackage.Parameters?["artWorkspace"] is not JsonObject workspace)
        {
            throw new ArgumentException(
                "局部编辑合同只能附加到 art-workspace 生成任务。",
                nameof(package));
        }

        workspace["editContract"] = BuildNode(safeContract);
        GenerationInputValidator.ValidateConstraintPackage(safePackage);
        return safePackage;
    }

    public static bool TryRead(
        GenerationConstraintPackage package,
        out ArtWorkspaceRegionEditContract? contract)
    {
        ArgumentNullException.ThrowIfNull(package);
        contract = null;
        if (package.Parameters?["artWorkspace"] is not JsonObject workspace ||
            workspace["editContract"] is not JsonObject node)
        {
            return false;
        }

        try
        {
            var bounds = node["sourcePixelBounds"] as JsonObject
                ?? throw new InvalidDataException("局部编辑合同缺少 sourcePixelBounds。");
            contract = Normalize(new ArtWorkspaceRegionEditContract
            {
                SchemaVersion = ReadRequiredInt32(node, "schemaVersion"),
                OperationMode = ReadRequiredString(node, "operationMode"),
                BaseImageId = ReadRequiredString(node, "baseImageId"),
                BaseImageSha256 = ReadRequiredString(node, "baseImageSha256"),
                BaseImagePath = ReadRequiredString(node, "baseImagePath"),
                MaskPath = ReadRequiredString(node, "maskPath"),
                MaskSha256 = ReadRequiredString(node, "maskSha256"),
                RegionPreviewPath = ReadRequiredString(node, "regionPreviewPath"),
                CompositePath = ReadRequiredString(node, "compositePath"),
                DifferenceReportPath = ReadRequiredString(node, "differenceReportPath"),
                DifferencePreviewPath = ReadRequiredString(node, "differencePreviewPath"),
                HistoryReferencePolicy = ReadRequiredString(node, "historyReferencePolicy"),
                PreserveOutsideMask = ReadRequiredBoolean(node, "preserveOutsideMask"),
                MaximumOutsideMaskChangedPercent = ReadRequiredDouble(
                    node,
                    "maximumOutsideMaskChangedPercent"),
                SourcePixelBounds = new ArtWorkspaceRegionEditPixelBounds
                {
                    X = ReadRequiredInt32(bounds, "x"),
                    Y = ReadRequiredInt32(bounds, "y"),
                    Width = ReadRequiredInt32(bounds, "width"),
                    Height = ReadRequiredInt32(bounds, "height")
                }
            });
            return true;
        }
        catch (Exception exception) when (exception is
                   ArgumentException or
                   InvalidDataException or
                   FormatException or
                   InvalidOperationException)
        {
            contract = null;
            return false;
        }
    }

    public static bool HasDeclaredContract(GenerationConstraintPackage package) =>
        package.Parameters?["artWorkspace"] is JsonObject workspace &&
        workspace.ContainsKey("editContract");

    private static ArtWorkspaceRegionEditTarget ReadTarget(
        JsonObject reference,
        bool sourceBoundsNestedUnderRegion)
    {
        var bounds = sourceBoundsNestedUnderRegion
            ? reference["region"]?["sourcePixelBounds"] as JsonObject
            : reference["sourcePixelBounds"] as JsonObject;
        if (bounds is null)
        {
            throw new InvalidDataException("局部编辑引用缺少 sourcePixelBounds。");
        }

        return new ArtWorkspaceRegionEditTarget
        {
            BaseImageId = ReadRequiredString(reference, "imageId"),
            BaseImageSha256 = ReadRequiredString(reference, "sha256").ToLowerInvariant(),
            BaseImageDataRootRelativePath = ReadRequiredString(
                reference,
                reference.ContainsKey("dataRootRelativePath")
                    ? "dataRootRelativePath"
                    : "snapshotPath"),
            BasePixelWidth = ReadRequiredInt32(reference, "pixelWidth"),
            BasePixelHeight = ReadRequiredInt32(reference, "pixelHeight"),
            SourcePixelBounds = new ArtWorkspaceRegionEditPixelBounds
            {
                X = ReadRequiredInt32(bounds, "x"),
                Y = ReadRequiredInt32(bounds, "y"),
                Width = ReadRequiredInt32(bounds, "width"),
                Height = ReadRequiredInt32(bounds, "height")
            }
        };
    }

    private static ArtWorkspaceRegionEditContract Normalize(ArtWorkspaceRegionEditContract contract)
    {
        if (contract.SchemaVersion != ArtWorkspaceRegionEditContract.CurrentSchemaVersion ||
            !string.Equals(
                contract.OperationMode,
                ArtWorkspaceRegionEditContract.RegionEditOperationMode,
                StringComparison.Ordinal) ||
            !Guid.TryParseExact(contract.BaseImageId, "N", out _) ||
            contract.BaseImageSha256.Length != 64 ||
            contract.BaseImageSha256.Any(character => !Uri.IsHexDigit(character)) ||
            contract.MaskSha256.Length != 64 ||
            contract.MaskSha256.Any(character => !Uri.IsHexDigit(character)) ||
            !string.Equals(
                contract.HistoryReferencePolicy,
                ArtWorkspaceRegionEditContract.ExcludeHistoryPolicy,
                StringComparison.Ordinal) ||
            !contract.PreserveOutsideMask ||
            !double.IsFinite(contract.MaximumOutsideMaskChangedPercent) ||
            contract.MaximumOutsideMaskChangedPercent is <= 0d or > 100d)
        {
            throw new ArgumentException("局部编辑合同版本、身份或保留策略无效。", nameof(contract));
        }

        var bounds = contract.SourcePixelBounds ??
            throw new ArgumentException("局部编辑合同缺少 sourcePixelBounds。", nameof(contract));
        if (bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentException("局部编辑 sourcePixelBounds 无效。", nameof(contract));
        }

        var baseImagePath = NormalizePath(contract.BaseImagePath);
        var maskPath = NormalizePath(contract.MaskPath);
        var regionPreviewPath = NormalizePath(contract.RegionPreviewPath);
        var compositePath = NormalizePath(contract.CompositePath);
        var differenceReportPath = NormalizePath(contract.DifferenceReportPath);
        var differencePreviewPath = NormalizePath(contract.DifferencePreviewPath);
        var artifactPaths = new[]
        {
            baseImagePath,
            maskPath,
            regionPreviewPath,
            compositePath,
            differenceReportPath,
            differencePreviewPath
        };
        if (artifactPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != artifactPaths.Length)
        {
            throw new ArgumentException(
                "局部编辑 base、mask、预览、合成与差异诊断必须使用互不重叠的独立路径。",
                nameof(contract));
        }

        if (!HasExtension(baseImagePath, ".png") ||
            !HasExtension(maskPath, ".png") ||
            !HasExtension(regionPreviewPath, ".png") ||
            !HasExtension(compositePath, ".png") ||
            !HasExtension(differencePreviewPath, ".png") ||
            !HasExtension(differenceReportPath, ".json"))
        {
            throw new ArgumentException(
                "局部编辑图片工件必须是 PNG，差异报告必须是 JSON。",
                nameof(contract));
        }

        return contract with
        {
            BaseImageSha256 = contract.BaseImageSha256.ToLowerInvariant(),
            MaskSha256 = contract.MaskSha256.ToLowerInvariant(),
            BaseImagePath = baseImagePath,
            MaskPath = maskPath,
            RegionPreviewPath = regionPreviewPath,
            CompositePath = compositePath,
            DifferenceReportPath = differenceReportPath,
            DifferencePreviewPath = differencePreviewPath,
            SourcePixelBounds = bounds with { }
        };
    }

    private static JsonObject BuildNode(ArtWorkspaceRegionEditContract contract) => new()
    {
        ["schemaVersion"] = contract.SchemaVersion,
        ["operationMode"] = contract.OperationMode,
        ["baseImageId"] = contract.BaseImageId,
        ["baseImageSha256"] = contract.BaseImageSha256,
        ["baseImagePath"] = contract.BaseImagePath,
        ["maskPath"] = contract.MaskPath,
        ["apiMaskPath"] = contract.MaskPath,
        ["maskSha256"] = contract.MaskSha256,
        ["regionPreviewPath"] = contract.RegionPreviewPath,
        ["compositePath"] = contract.CompositePath,
        ["differenceReportPath"] = contract.DifferenceReportPath,
        ["differencePreviewPath"] = contract.DifferencePreviewPath,
        ["historyReferencePolicy"] = contract.HistoryReferencePolicy,
        ["preserveOutsideMask"] = contract.PreserveOutsideMask,
        ["maskAlphaSemantics"] = "transparent-edit_opaque-preserve",
        ["maximumOutsideMaskChangedPercent"] = contract.MaximumOutsideMaskChangedPercent,
        ["sourcePixelBounds"] = new JsonObject
        {
            ["x"] = contract.SourcePixelBounds.X,
            ["y"] = contract.SourcePixelBounds.Y,
            ["width"] = contract.SourcePixelBounds.Width,
            ["height"] = contract.SourcePixelBounds.Height
        }
    };

    private static string NormalizePath(string value) =>
        GenerationInputValidator.NormalizeRelativeAssetPath(value);

    private static bool HasExtension(string path, string extension) =>
        string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    private static string ReadRequiredString(JsonObject node, string name) =>
        node[name]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"局部编辑合同缺少 {name}。");

    private static string? ReadOptionalString(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static int ReadRequiredInt32(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<int>(out var number)
            ? number
            : throw new InvalidDataException($"局部编辑合同缺少有效的 {name}。");

    private static bool ReadRequiredBoolean(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag
            : throw new InvalidDataException($"局部编辑合同缺少有效的 {name}。");

    private static double ReadRequiredDouble(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<double>(out var number)
            ? number
            : throw new InvalidDataException($"局部编辑合同缺少有效的 {name}。");
}
