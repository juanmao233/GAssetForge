using System.Text.Json.Nodes;

namespace GAssetForge.Core.Generation;

public sealed record MaterialGenerationStyleReference
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public string MediaType { get; init; } = string.Empty;

    public string DataRootRelativePath { get; init; } = string.Empty;
}

public sealed record MaterialGenerationStyleSelection
{
    public string GroupId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Prompt { get; init; } = string.Empty;

    public MaterialGenerationStyleReference[] References { get; init; } = [];
}

public sealed record GenerationDeliveryPlan
{
    public string AssetRoot { get; init; } = string.Empty;

    public string OutputDirectory { get; init; } = string.Empty;

    public string[] TargetPaths { get; init; } = [];

    /// <summary>
    /// Preallocated source-image paths used only by the local post-processing pipeline.
    /// Empty for legacy/direct-alpha tasks.
    /// </summary>
    public string[] StagingPaths { get; init; } = [];

    public string SuggestedBaseName { get; init; } = string.Empty;

    /// <summary>
    /// Optional project-local run workspace owned and pre-created by GAssetForge. Legacy
    /// tasks omit this object and retain the previous delivery behavior.
    /// </summary>
    public GenerationRunWorkspacePlan? Workspace { get; init; }
}

public static class MaterialGenerationConstraintComposer
{
    public const string StyleRuleKey = "material.style.selection";
    public const string DeliveryRuleKey = "material.delivery.targets";
    public const int MaximumReferenceCount = 128;

    private const int ContractSchemaVersion = 1;
    private const string BackgroundStagingDirectoryName = ProductIdentity.BackgroundStagingDirectoryName;

    public static GenerationConstraintPackage Compose(
        GenerationConstraintPackage basePackage,
        MaterialGenerationStyleSelection style,
        GenerationDeliveryPlan delivery)
    {
        ArgumentNullException.ThrowIfNull(basePackage);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(delivery);

        var safeBase = GenerationJson.Clone(basePackage);
        GenerationInputValidator.ValidateConstraintPackage(safeBase);
        var safeStyle = NormalizeStyle(style);
        var safeDelivery = NormalizeDelivery(delivery, safeBase.Output);

        var parameters = safeBase.Parameters.DeepClone().AsObject();
        parameters["style"] = BuildStyleNode(safeStyle);
        parameters["delivery"] = BuildDeliveryNode(safeDelivery);

        var rules = safeBase.Rules
            .Where(rule =>
                !string.Equals(rule.Key, StyleRuleKey, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rule.Key, DeliveryRuleKey, StringComparison.OrdinalIgnoreCase))
            .Concat(
            [
                new GenerationConstraintRule
                {
                    Key = StyleRuleKey,
                    Category = "style",
                    Instruction =
                        $"必须采用风格组“{safeStyle.Name}”的风格定义；完整描述、提示词和 {safeStyle.References.Length} 张参考图快照位于 parameters.style。",
                    Required = true
                },
                new GenerationConstraintRule
                {
                    Key = DeliveryRuleKey,
                    Category = "output",
                    Instruction = BuildDeliveryInstruction(safeDelivery),
                    Required = true
                }
            ])
            .ToArray();

        var result = safeBase with
        {
            Parameters = parameters,
            Rules = rules
        };
        GenerationInputValidator.ValidateConstraintPackage(result);
        return result;
    }

    /// <summary>
    /// Reallocates immutable delivery targets while preserving the original prompt, style snapshot,
    /// workflow parameters and output requirements. Used when a failed row is retried as a new task.
    /// </summary>
    public static GenerationConstraintPackage ReplaceDelivery(
        GenerationConstraintPackage package,
        GenerationDeliveryPlan delivery)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(delivery);

        var safePackage = GenerationJson.Clone(package);
        GenerationInputValidator.ValidateConstraintPackage(safePackage);
        var safeDelivery = NormalizeDelivery(delivery, safePackage.Output);
        var parameters = safePackage.Parameters.DeepClone().AsObject();
        parameters["delivery"] = BuildDeliveryNode(safeDelivery);
        var rules = safePackage.Rules
            .Where(rule => !string.Equals(
                rule.Key,
                DeliveryRuleKey,
                StringComparison.OrdinalIgnoreCase))
            .Append(new GenerationConstraintRule
            {
                Key = DeliveryRuleKey,
                Category = "output",
                Instruction = BuildDeliveryInstruction(safeDelivery),
                Required = true
            })
            .ToArray();
        var result = safePackage with
        {
            Parameters = parameters,
            Rules = rules
        };
        GenerationInputValidator.ValidateConstraintPackage(result);
        return result;
    }

    public static bool TryReadDelivery(
        GenerationConstraintPackage package,
        out GenerationDeliveryPlan? delivery)
    {
        ArgumentNullException.ThrowIfNull(package);
        delivery = null;
        if (package.Parameters?["delivery"] is not JsonObject node)
        {
            return false;
        }

        try
        {
            if (!node.TryGetPropertyValue("schemaVersion", out var schemaNode) ||
                schemaNode is not JsonValue schemaValue ||
                !schemaValue.TryGetValue<int>(out var schemaVersion) ||
                schemaVersion != ContractSchemaVersion)
            {
                return false;
            }

            if (!TryReadString(node, "assetRoot", out var assetRoot) ||
                !TryReadString(node, "outputDirectory", out var outputDirectory) ||
                !TryReadString(node, "suggestedBaseName", out var suggestedBaseName) ||
                node["targetPaths"] is not JsonArray targetArray)
            {
                return false;
            }

            var targetPaths = new string[targetArray.Count];
            for (var index = 0; index < targetArray.Count; index++)
            {
                if (targetArray[index] is not JsonValue value ||
                    !value.TryGetValue<string>(out var targetPath) ||
                    string.IsNullOrWhiteSpace(targetPath))
                {
                    return false;
                }

                targetPaths[index] = targetPath;
            }

            var stagingPaths = Array.Empty<string>();
            if (node["stagingPaths"] is JsonArray stagingArray)
            {
                stagingPaths = new string[stagingArray.Count];
                for (var index = 0; index < stagingArray.Count; index++)
                {
                    if (stagingArray[index] is not JsonValue value ||
                        !value.TryGetValue<string>(out var stagingPath) ||
                        string.IsNullOrWhiteSpace(stagingPath))
                    {
                        return false;
                    }

                    stagingPaths[index] = stagingPath;
                }
            }

            GenerationRunWorkspacePlan? workspace = null;
            if (node["workspace"] is JsonObject workspaceNode)
            {
                workspace = ReadWorkspace(workspaceNode);
            }

            delivery = NormalizeDelivery(
                new GenerationDeliveryPlan
                {
                    AssetRoot = assetRoot,
                    OutputDirectory = outputDirectory,
                    TargetPaths = targetPaths,
                    StagingPaths = stagingPaths,
                    SuggestedBaseName = suggestedBaseName,
                    Workspace = workspace
                },
                package.Output);
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidOperationException or
            InvalidDataException or
            FormatException or
            NotSupportedException or
            PathTooLongException)
        {
            delivery = null;
            return false;
        }
    }

    private static MaterialGenerationStyleSelection NormalizeStyle(
        MaterialGenerationStyleSelection style)
    {
        var groupId = NormalizeRequiredText(style.GroupId, nameof(style.GroupId), 128);
        var name = NormalizeRequiredText(style.Name, nameof(style.Name), 160);
        var description = NormalizeOptionalText(style.Description, nameof(style.Description), 8_000);
        var prompt = NormalizeOptionalText(style.Prompt, nameof(style.Prompt), 32_000);
        var references = style.References
            ?? throw new ArgumentException("风格参考图集合不能为空。", nameof(style));
        if (references.Length > MaximumReferenceCount)
        {
            throw new ArgumentException($"单个生成任务最多携带 {MaximumReferenceCount} 张风格参考图。", nameof(style));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedReferences = new MaterialGenerationStyleReference[references.Length];
        for (var index = 0; index < references.Length; index++)
        {
            var reference = references[index]
                ?? throw new ArgumentException("风格参考图集合不能包含空项。", nameof(style));
            var id = NormalizeRequiredText(reference.Id, nameof(reference.Id), 128);
            var referenceName = NormalizeRequiredText(reference.Name, nameof(reference.Name), 260);
            var sha256 = NormalizeSha256(reference.Sha256);
            var mediaType = NormalizeMediaType(reference.MediaType);
            var relativePath = GenerationInputValidator.NormalizeRelativeAssetPath(
                reference.DataRootRelativePath);

            if (!ids.Add(id))
            {
                throw new ArgumentException($"风格参考图 ID 重复：{id}。", nameof(style));
            }

            if (!paths.Add(relativePath))
            {
                throw new ArgumentException($"风格参考图路径重复：{relativePath}。", nameof(style));
            }

            normalizedReferences[index] = reference with
            {
                Id = id,
                Name = referenceName,
                Sha256 = sha256,
                MediaType = mediaType,
                DataRootRelativePath = relativePath
            };
        }

        return style with
        {
            GroupId = groupId,
            Name = name,
            Description = description,
            Prompt = prompt,
            References = normalizedReferences
        };
    }

    private static GenerationDeliveryPlan NormalizeDelivery(
        GenerationDeliveryPlan delivery,
        GenerationOutputSpec output)
    {
        if (string.IsNullOrWhiteSpace(delivery.AssetRoot) ||
            !Path.IsPathFullyQualified(delivery.AssetRoot))
        {
            throw new ArgumentException("交付计划的 AssetRoot 必须是绝对路径。", nameof(delivery));
        }

        var assetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(delivery.AssetRoot));
        var volumeRoot = Path.GetPathRoot(assetRoot);
        if (!string.IsNullOrWhiteSpace(volumeRoot) && PathsEqual(assetRoot, volumeRoot))
        {
            throw new ArgumentException("交付计划不能直接使用文件系统根目录。", nameof(delivery));
        }

        var outputDirectory = NormalizeOutputDirectory(delivery.OutputDirectory);
        var targetPaths = delivery.TargetPaths
            ?? throw new ArgumentException("交付目标路径集合不能为空。", nameof(delivery));
        if (targetPaths.Length != output.Count)
        {
            throw new ArgumentException(
                $"交付目标路径数量必须与输出数量 {output.Count} 一致。",
                nameof(delivery));
        }

        var normalizedTargets = targetPaths
            .Select(GenerationInputValidator.NormalizeRelativeAssetPath)
            .ToArray();
        if (normalizedTargets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedTargets.Length)
        {
            throw new ArgumentException("交付目标路径不能重复。", nameof(delivery));
        }

        foreach (var targetPath in normalizedTargets)
        {
            var isInsideOutputDirectory = outputDirectory.Length == 0
                ? !targetPath.Contains('/')
                : targetPath.StartsWith($"{outputDirectory}/", StringComparison.OrdinalIgnoreCase);
            if (!isInsideOutputDirectory)
            {
                throw new ArgumentException(
                    outputDirectory.Length == 0
                        ? $"输出目录为 Assets 根目录时，交付目标必须是直接文件名：{targetPath}。"
                        : $"交付目标必须位于 outputDirectory 内：{targetPath}。",
                    nameof(delivery));
            }

            EnsureTargetFormat(targetPath, output.Format);
        }

        var suppliedStagingPaths = delivery.StagingPaths ?? [];
        string[] normalizedStagingPaths;
        if (output.BackgroundRemoval is null)
        {
            if (suppliedStagingPaths.Length != 0)
            {
                throw new ArgumentException("未启用背景剔除的任务不能包含 staging 路径。", nameof(delivery));
            }

            normalizedStagingPaths = [];
        }
        else
        {
            var stagingPaths = suppliedStagingPaths.Length == 0
                ? normalizedTargets.Select(BuildBackgroundStagingPath).ToArray()
                : suppliedStagingPaths;
            if (stagingPaths.Length != output.Count)
            {
                throw new ArgumentException(
                    $"背景剔除 staging 路径数量必须与输出数量 {output.Count} 一致。",
                    nameof(delivery));
            }

            normalizedStagingPaths = stagingPaths
                .Select(GenerationInputValidator.NormalizeRelativeAssetPath)
                .ToArray();
            if (normalizedStagingPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedStagingPaths.Length)
            {
                throw new ArgumentException("背景剔除 staging 路径不能重复。", nameof(delivery));
            }

            var finalTargets = normalizedTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var workspaceRoot = delivery.Workspace is null
                ? null
                : GenerationInputValidator.NormalizeRelativeAssetPath(delivery.Workspace.RootPath);
            foreach (var stagingPath in normalizedStagingPaths)
            {
                if (finalTargets.Contains(stagingPath))
                {
                    throw new ArgumentException("背景剔除 staging 路径不能与最终交付路径相同。", nameof(delivery));
                }

                var isInsideLegacyStagingDirectory =
                    IsInsideStagingDirectory(stagingPath, outputDirectory, BackgroundStagingDirectoryName) ||
                    IsInsideStagingDirectory(
                        stagingPath,
                        outputDirectory,
                        ProductIdentity.LegacyBackgroundStagingDirectoryName);
                var isInsideRunWorkspace = workspaceRoot is not null &&
                    stagingPath.StartsWith(
                        $"{workspaceRoot}/35-normalized-input/",
                        StringComparison.OrdinalIgnoreCase);
                if ((!isInsideLegacyStagingDirectory && !isInsideRunWorkspace) ||
                    !string.Equals(Path.GetExtension(stagingPath), ".png", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"背景剔除 staging 路径必须位于旧 staging 目录或运行工作区 35-normalized-input 中并使用 PNG：{stagingPath}。",
                        nameof(delivery));
                }
            }
        }

        var suggestedBaseName = NormalizeSuggestedBaseName(delivery.SuggestedBaseName);
        var workspace = delivery.Workspace is null
            ? null
            : NormalizeWorkspace(delivery.Workspace, output, normalizedTargets, normalizedStagingPaths);
        return delivery with
        {
            AssetRoot = assetRoot,
            OutputDirectory = outputDirectory,
            TargetPaths = normalizedTargets,
            StagingPaths = normalizedStagingPaths,
            SuggestedBaseName = suggestedBaseName,
            Workspace = workspace
        };
    }

    private static JsonObject BuildStyleNode(MaterialGenerationStyleSelection style)
    {
        var referenceNodes = new JsonArray();
        foreach (var reference in style.References)
        {
            referenceNodes.Add(new JsonObject
            {
                ["id"] = reference.Id,
                ["name"] = reference.Name,
                ["sha256"] = reference.Sha256,
                ["mediaType"] = reference.MediaType,
                ["dataRootRelativePath"] = reference.DataRootRelativePath
            });
        }

        return new JsonObject
        {
            ["schemaVersion"] = ContractSchemaVersion,
            ["groupId"] = style.GroupId,
            ["name"] = style.Name,
            ["description"] = style.Description,
            ["prompt"] = style.Prompt,
            ["references"] = referenceNodes
        };
    }

    private static JsonObject BuildDeliveryNode(GenerationDeliveryPlan delivery)
    {
        var targetNodes = new JsonArray();
        foreach (var targetPath in delivery.TargetPaths)
        {
            targetNodes.Add(targetPath);
        }

        var stagingNodes = new JsonArray();
        foreach (var stagingPath in delivery.StagingPaths)
        {
            stagingNodes.Add(stagingPath);
        }

        var node = new JsonObject
        {
            ["schemaVersion"] = ContractSchemaVersion,
            ["assetRoot"] = delivery.AssetRoot,
            ["outputDirectory"] = delivery.OutputDirectory,
            ["targetPaths"] = targetNodes,
            ["suggestedBaseName"] = delivery.SuggestedBaseName
        };
        if (stagingNodes.Count > 0)
        {
            node["stagingPaths"] = stagingNodes;
        }

        if (delivery.Workspace is not null)
        {
            node["workspace"] = BuildWorkspaceNode(delivery.Workspace);
        }

        return node;
    }

    private static string BuildDeliveryInstruction(GenerationDeliveryPlan delivery) =>
        delivery.Workspace is not null
            ? $"GAssetForge 已预建项目内运行工作区 {delivery.Workspace.RootPath}。Agent 只需调用真实图像模型，并把模型返回文件交给 submit_generation_output；目录创建、导入、归一化、背景剔除、校验和最终提交均由软件完成。"
            : delivery.StagingPaths.Length == 0
            ? $"必须生成 {delivery.TargetPaths.Length} 个产物，并严格写入 parameters.delivery.targetPaths 指定的相对路径；不得以其它已有文件代替。"
            : $"必须生成 {delivery.TargetPaths.Length} 个带指定纯色底的原图，并按索引严格写入 parameters.delivery.stagingPaths；随后逐张调用 GAssetForge 本地输出处理工具，最终透明 PNG 才会写入 parameters.delivery.targetPaths。";

    private static GenerationRunWorkspacePlan ReadWorkspace(JsonObject node)
    {
        if (!node.TryGetPropertyValue("schemaVersion", out var schemaNode) ||
            schemaNode is not JsonValue schemaValue ||
            !schemaValue.TryGetValue<int>(out var schemaVersion) ||
            schemaVersion != GenerationRunWorkspacePlan.CurrentSchemaVersion ||
            !TryReadString(node, "runId", out var runId) ||
            !TryReadString(node, "rootPath", out var rootPath) ||
            !TryReadString(node, "manifestPath", out var manifestPath) ||
            !TryReadString(node, "promptPath", out var promptPath) ||
            !TryReadString(node, "diagnosticsPath", out var diagnosticsPath) ||
            node["modelOutputPaths"] is not JsonArray modelOutputArray ||
            node["processedPaths"] is not JsonArray processedArray)
        {
            throw new InvalidDataException("生成运行工作区契约损坏或版本不受支持。");
        }

        var preserveIntermediates = true;
        if (node["preserveIntermediates"] is JsonValue preserveValue &&
            !preserveValue.TryGetValue<bool>(out preserveIntermediates))
        {
            throw new InvalidDataException("生成运行工作区 preserveIntermediates 无效。");
        }

        return new GenerationRunWorkspacePlan
        {
            SchemaVersion = schemaVersion,
            RunId = runId,
            RootPath = rootPath,
            ManifestPath = manifestPath,
            PromptPath = promptPath,
            DiagnosticsPath = diagnosticsPath,
            ModelOutputPaths = ReadStringArray(modelOutputArray, "modelOutputPaths"),
            ProcessedPaths = ReadStringArray(processedArray, "processedPaths"),
            PreserveIntermediates = preserveIntermediates
        };
    }

    private static GenerationRunWorkspacePlan NormalizeWorkspace(
        GenerationRunWorkspacePlan workspace,
        GenerationOutputSpec output,
        IReadOnlyCollection<string> targetPaths,
        IReadOnlyCollection<string> stagingPaths)
    {
        if (workspace.SchemaVersion != GenerationRunWorkspacePlan.CurrentSchemaVersion ||
            !Guid.TryParseExact(workspace.RunId, "N", out _))
        {
            throw new ArgumentException("生成运行工作区版本或 RunId 无效。", nameof(workspace));
        }

        var rootPath = GenerationInputValidator.NormalizeRelativeAssetPath(workspace.RootPath);
        var manifestPath = NormalizeWorkspaceChild(rootPath, workspace.ManifestPath, "manifestPath");
        var promptPath = NormalizeWorkspaceChild(rootPath, workspace.PromptPath, "promptPath");
        var diagnosticsPath = NormalizeWorkspaceChild(rootPath, workspace.DiagnosticsPath, "diagnosticsPath");
        var modelOutputPaths = NormalizeWorkspaceOutputs(
            rootPath,
            workspace.ModelOutputPaths,
            output.Count,
            "modelOutputPaths");
        var processedPaths = NormalizeWorkspaceOutputs(
            rootPath,
            workspace.ProcessedPaths,
            output.Count,
            "processedPaths");

        var allPaths = targetPaths
            .Concat(stagingPaths)
            .Concat(modelOutputPaths)
            .Concat(processedPaths)
            .ToArray();
        if (allPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != allPaths.Length)
        {
            throw new ArgumentException("运行工作区路径不能与 staging 或最终交付路径重复。", nameof(workspace));
        }

        return workspace with
        {
            RunId = workspace.RunId.ToLowerInvariant(),
            RootPath = rootPath,
            ManifestPath = manifestPath,
            PromptPath = promptPath,
            DiagnosticsPath = diagnosticsPath,
            ModelOutputPaths = modelOutputPaths,
            ProcessedPaths = processedPaths
        };
    }

    private static string NormalizeWorkspaceChild(string rootPath, string path, string name)
    {
        var normalized = GenerationInputValidator.NormalizeRelativeAssetPath(path);
        if (!normalized.StartsWith($"{rootPath}/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"运行工作区 {name} 必须位于 rootPath 内。", name);
        }

        return normalized;
    }

    private static string[] NormalizeWorkspaceOutputs(
        string rootPath,
        string[]? paths,
        int expectedCount,
        string name)
    {
        if (paths is null || paths.Length != expectedCount)
        {
            throw new ArgumentException($"运行工作区 {name} 必须与 output.count 一一对应。", name);
        }

        var normalized = paths
            .Select(path => NormalizeWorkspaceChild(rootPath, path, name))
            .ToArray();
        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
        {
            throw new ArgumentException($"运行工作区 {name} 不能包含重复路径。", name);
        }

        return normalized;
    }

    private static JsonObject BuildWorkspaceNode(GenerationRunWorkspacePlan workspace)
    {
        var modelOutputNodes = new JsonArray();
        foreach (var path in workspace.ModelOutputPaths)
        {
            modelOutputNodes.Add(path);
        }

        var processedNodes = new JsonArray();
        foreach (var path in workspace.ProcessedPaths)
        {
            processedNodes.Add(path);
        }

        return new JsonObject
        {
            ["schemaVersion"] = workspace.SchemaVersion,
            ["runId"] = workspace.RunId,
            ["rootPath"] = workspace.RootPath,
            ["manifestPath"] = workspace.ManifestPath,
            ["promptPath"] = workspace.PromptPath,
            ["diagnosticsPath"] = workspace.DiagnosticsPath,
            ["modelOutputPaths"] = modelOutputNodes,
            ["processedPaths"] = processedNodes,
            ["preserveIntermediates"] = workspace.PreserveIntermediates
        };
    }

    private static string[] ReadStringArray(JsonArray array, string name)
    {
        var result = new string[array.Count];
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonValue value ||
                !value.TryGetValue<string>(out var path) ||
                string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException($"生成运行工作区 {name} 包含无效路径。");
            }

            result[index] = path;
        }

        return result;
    }

    private static string BuildBackgroundStagingPath(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath.Replace('/', Path.DirectorySeparatorChar))
            ?.Replace('\\', '/') ?? string.Empty;
        var fileName = $"{Path.GetFileNameWithoutExtension(targetPath)}.pfraw.png";
        return string.IsNullOrEmpty(directory)
            ? $"{BackgroundStagingDirectoryName}/{fileName}"
            : $"{directory}/{BackgroundStagingDirectoryName}/{fileName}";
    }

    private static bool IsInsideStagingDirectory(
        string stagingPath,
        string outputDirectory,
        string stagingDirectoryName) =>
        outputDirectory.Length == 0
            ? stagingPath.StartsWith($"{stagingDirectoryName}/", StringComparison.OrdinalIgnoreCase)
            : stagingPath.StartsWith(
                $"{outputDirectory}/{stagingDirectoryName}/",
                StringComparison.OrdinalIgnoreCase);

    private static bool TryReadString(JsonObject node, string name, out string value)
    {
        value = string.Empty;
        if (node[name] is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<string>(out var candidate) ||
            candidate is null)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static string NormalizeRequiredText(string? value, string name, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 || normalized.Length > maximumLength ||
            normalized.Any(character => character == '\0' ||
                char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException($"{name} 为空、过长或包含无效字符。", name);
        }

        return normalized;
    }

    private static string NormalizeOptionalText(string? value, string name, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maximumLength ||
            normalized.Any(character => character == '\0' ||
                char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException($"{name} 过长或包含无效字符。", name);
        }

        return normalized;
    }

    private static string NormalizeSha256(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("风格参考图 SHA-256 必须是 64 位十六进制字符串。", nameof(value));
        }

        return normalized;
    }

    private static string NormalizeMediaType(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length is < 7 or > 128 ||
            !normalized.StartsWith("image/", StringComparison.Ordinal) ||
            normalized.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new ArgumentException("风格参考图 MediaType 必须是有效的 image/* 类型。", nameof(value));
        }

        return normalized;
    }

    private static string NormalizeSuggestedBaseName(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 160 ||
            normalized is "." or ".." ||
            normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentException("SuggestedBaseName 不是有效的文件基础名称。", nameof(value));
        }

        return normalized;
    }

    private static string NormalizeOutputDirectory(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized is "" or "."
            ? string.Empty
            : GenerationInputValidator.NormalizeRelativeAssetPath(normalized);
    }

    private static void EnsureTargetFormat(string targetPath, string requestedFormat)
    {
        var normalizedFormat = requestedFormat.Trim().TrimStart('.').ToLowerInvariant();
        var extension = Path.GetExtension(targetPath);
        var matches = normalizedFormat switch
        {
            "jpg" or "jpeg" => extension is ".jpg" or ".jpeg" ||
                                extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase),
            "tif" or "tiff" => extension is ".tif" or ".tiff" ||
                                extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                                extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase),
            _ => extension.Equals($".{normalizedFormat}", StringComparison.OrdinalIgnoreCase)
        };

        if (!matches)
        {
            throw new ArgumentException(
                $"交付目标格式不匹配：任务要求 {requestedFormat}，目标为 {targetPath}。",
                nameof(targetPath));
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
