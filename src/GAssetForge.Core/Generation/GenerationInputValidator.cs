using System.Text.Json;
using GAssetForge.Core.Imaging;

namespace GAssetForge.Core.Generation;

public static class GenerationInputValidator
{
    public const int MaximumOutputCount = 16;
    public const int MaximumBatchItemCount = MaximumBatchOutputCount;
    public const int MaximumBatchOutputCount = 256;
    public const int MaximumAttempts = 11;

    private const int MaximumPromptLength = 65_536;
    private const int MaximumNegativePromptLength = 32_768;
    private const int MaximumParametersUtf8Bytes = 262_144;
    private const int MaximumRelativePathLength = 1_024;
    private const int MaximumAutomaticRetryBlockedReasonLength = 4_096;

    private static readonly char[] InvalidAssetPathCharacters = ['\0', ':', '*', '?', '"', '<', '>', '|'];

    public static void ValidateCreateRequest(CreateGenerationTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.DisplayName, nameof(request.DisplayName), 1, 256);

        if (request.RequestedBy is not null)
        {
            ValidateText(request.RequestedBy, nameof(request.RequestedBy), 1, 128);
        }

        if (request.Constraints is null)
        {
            throw new ArgumentException("约束包不能为空。", nameof(request));
        }

        ValidateMaxAttempts(request.MaxAttempts, nameof(request.MaxAttempts));

        ValidateConstraintPackage(request.Constraints);
    }

    public static void ValidateCreateBatchRequest(CreateGenerationBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCanonicalGuid(request.BatchId, nameof(request.BatchId), "批次 ID");
        ValidateText(request.DisplayName, nameof(request.DisplayName), 1, 256);

        if (request.RequestedBy is not null)
        {
            ValidateText(request.RequestedBy, nameof(request.RequestedBy), 1, 128);
        }

        if (request.Items is null)
        {
            throw new ArgumentException("生成批次项目集合不能为空。", nameof(request));
        }

        if (request.Items.Length is < 1 or > MaximumBatchItemCount)
        {
            throw new ArgumentException(
                $"单个生成批次必须包含 1 到 {MaximumBatchItemCount} 行。",
                nameof(request));
        }

        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        var totalOutputCount = 0;
        foreach (var item in request.Items)
        {
            if (item is null)
            {
                throw new ArgumentException("生成批次项目集合不能包含空项。", nameof(request));
            }

            ValidateCanonicalGuid(item.ItemId, nameof(item.ItemId), "批次项目 ID");
            if (!itemIds.Add(item.ItemId))
            {
                throw new ArgumentException($"生成批次项目 ID 重复：{item.ItemId}。", nameof(request));
            }

            ValidateText(item.DisplayName, nameof(item.DisplayName), 1, 256);
            ValidateMaxAttempts(item.MaxAttempts, nameof(item.MaxAttempts));
            if (item.Constraints is null)
            {
                throw new ArgumentException($"生成批次项目 {item.ItemId} 缺少约束包。", nameof(request));
            }

            ValidateConstraintPackage(item.Constraints);
            totalOutputCount = checked(totalOutputCount + item.Constraints.Output.Count);
            if (totalOutputCount > MaximumBatchOutputCount)
            {
                throw new ArgumentException(
                    $"单个生成批次最多请求 {MaximumBatchOutputCount} 个产物。",
                    nameof(request));
            }
        }
    }

    public static void ValidateCreateRetryBatchRequest(CreateGenerationRetryBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCanonicalGuid(request.BatchId, nameof(request.BatchId), "重试批次 ID");
        ValidateText(request.DisplayName, nameof(request.DisplayName), 1, 256);
        if (request.RequestedBy is not null)
        {
            ValidateText(request.RequestedBy, nameof(request.RequestedBy), 1, 128);
        }

        if (request.Items is null || request.Items.Length is < 1 or > MaximumBatchItemCount)
        {
            throw new ArgumentException(
                $"单个重试批次必须包含 1 到 {MaximumBatchItemCount} 行。",
                nameof(request));
        }

        var parentIds = new HashSet<string>(StringComparer.Ordinal);
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        var totalOutputCount = 0;
        foreach (var item in request.Items)
        {
            if (item is null)
            {
                throw new ArgumentException("重试批次项目集合不能包含空项。", nameof(request));
            }

            var parentTaskId = NormalizeTaskId(item.ParentTaskId);
            if (!parentIds.Add(parentTaskId))
            {
                throw new ArgumentException($"重试批次父任务重复：{parentTaskId}。", nameof(request));
            }

            ValidateCanonicalGuid(item.ItemId, nameof(item.ItemId), "重试批次项目 ID");
            if (!itemIds.Add(item.ItemId))
            {
                throw new ArgumentException($"重试批次项目 ID 重复：{item.ItemId}。", nameof(request));
            }

            ValidateText(item.DisplayName, nameof(item.DisplayName), 1, 256);
            if (item.Constraints is null)
            {
                throw new ArgumentException($"重试批次项目 {item.ItemId} 缺少约束包。", nameof(request));
            }

            ValidateConstraintPackage(item.Constraints);
            totalOutputCount = checked(totalOutputCount + item.Constraints.Output.Count);
            if (totalOutputCount > MaximumBatchOutputCount)
            {
                throw new ArgumentException(
                    $"单个重试批次最多请求 {MaximumBatchOutputCount} 个产物。",
                    nameof(request));
            }
        }
    }

    public static void ValidateConstraintPackage(GenerationConstraintPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (package.SchemaVersion != GenerationConstraintPackage.CurrentSchemaVersion)
        {
            throw new ArgumentException($"不支持的约束包版本：{package.SchemaVersion}。", nameof(package));
        }

        ValidateToken(package.WorkflowId, nameof(package.WorkflowId), 128, allowSlash: true);

        if (package.PreferredSkill is not null)
        {
            ValidateToken(package.PreferredSkill, nameof(package.PreferredSkill), 128, allowSlash: true, allowColon: true);
        }

        ValidateText(package.Prompt, nameof(package.Prompt), 1, MaximumPromptLength);

        if (package.NegativePrompt is not null)
        {
            ValidateText(package.NegativePrompt, nameof(package.NegativePrompt), 1, MaximumNegativePromptLength);
        }

        ValidateOutput(package.Output);

        if (package.Rules is null)
        {
            throw new ArgumentException("约束规则集合不能为空。", nameof(package));
        }

        if (package.Rules.Length > 128)
        {
            throw new ArgumentException("单个约束包最多包含 128 条规则。", nameof(package));
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in package.Rules)
        {
            if (rule is null)
            {
                throw new ArgumentException("约束规则不能包含空项。", nameof(package));
            }

            ValidateToken(rule.Key, nameof(rule.Key), 128, allowSlash: true);
            ValidateToken(rule.Category, nameof(rule.Category), 64, allowSlash: true);
            ValidateText(rule.Instruction, nameof(rule.Instruction), 1, 8_192);

            if (!keys.Add(rule.Key))
            {
                throw new ArgumentException($"约束规则键重复：{rule.Key}。", nameof(package));
            }

            if (rule.Weight is { } weight && (!double.IsFinite(weight) || weight is < 0d or > 1d))
            {
                throw new ArgumentException($"规则 {rule.Key} 的权重必须位于 0 到 1 之间。", nameof(package));
            }
        }

        if (package.Parameters is null)
        {
            throw new ArgumentException("结构化约束参数不能为空。", nameof(package));
        }

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(package.Parameters, GenerationJson.Options);
            if (bytes.Length > MaximumParametersUtf8Bytes)
            {
                throw new ArgumentException($"结构化约束参数不能超过 {MaximumParametersUtf8Bytes} UTF-8 字节。", nameof(package));
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("结构化约束参数不是有效 JSON。", nameof(package), exception);
        }
    }

    public static string ValidateAgentId(string agentId)
    {
        ValidateToken(agentId, nameof(agentId), 128, allowSlash: true, allowColon: true);
        return agentId;
    }

    public static string ValidateWorkflowId(string workflowId)
    {
        ValidateToken(workflowId, nameof(workflowId), 128, allowSlash: true);
        return workflowId;
    }

    public static string ValidateFailureMessage(string failureMessage)
    {
        ValidateText(failureMessage, nameof(failureMessage), 1, 8_192);
        return failureMessage;
    }

    public static string ValidateFailureCode(string failureCode)
    {
        ValidateFailureToken(failureCode, nameof(failureCode));
        return failureCode;
    }

    public static string ValidateFailureStage(string failureStage)
    {
        ValidateFailureToken(failureStage, nameof(failureStage));
        return failureStage;
    }

    public static string NormalizeTaskId(string taskId)
    {
        if (!Guid.TryParseExact(taskId, "N", out var id))
        {
            throw new ArgumentException("任务 ID 必须是 32 位 GUID。", nameof(taskId));
        }

        return id.ToString("N");
    }

    public static string NormalizeBatchId(string batchId)
    {
        ValidateCanonicalGuid(batchId, nameof(batchId), "批次 ID");
        return batchId;
    }

    public static string NormalizeRelativeAssetPath(string relativeAssetPath)
    {
        ValidateText(relativeAssetPath, nameof(relativeAssetPath), 1, MaximumRelativePathLength);

        if (Path.IsPathRooted(relativeAssetPath) ||
            relativeAssetPath.StartsWith('/') ||
            relativeAssetPath.StartsWith('\\'))
        {
            throw new ArgumentException("资产路径必须是相对路径。", nameof(relativeAssetPath));
        }

        var normalized = relativeAssetPath.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.IndexOfAny(InvalidAssetPathCharacters) >= 0 ||
                segment.Any(char.IsControl)))
        {
            throw new ArgumentException("资产路径包含空段、目录穿越或无效字符。", nameof(relativeAssetPath));
        }

        return string.Join('/', segments);
    }

    internal static void ValidatePersistedTask(GenerationTask task)
    {
        try
        {
            if (task.SchemaVersion != GenerationTask.CurrentSchemaVersion)
            {
                throw new InvalidDataException($"不支持的生成任务版本：{task.SchemaVersion}。");
            }

            if (!string.Equals(task.Id, NormalizeTaskId(task.Id), StringComparison.Ordinal))
            {
                throw new InvalidDataException($"生成任务 ID 未规范化：{task.Id}。");
            }

            if (task.Revision < 1)
            {
                throw new InvalidDataException($"生成任务 {task.Id} 的修订号无效。");
            }

            ValidateText(task.DisplayName, nameof(task.DisplayName), 1, 256);
            if (task.RequestedBy is not null)
            {
                ValidateText(task.RequestedBy, nameof(task.RequestedBy), 1, 128);
            }


            ValidateBatchMetadata(task);

            if (task.Constraints is null)
            {
                throw new InvalidDataException($"生成任务 {task.Id} 缺少约束包。");
            }

            ValidateConstraintPackage(task.Constraints);

            if (!Enum.IsDefined(task.Status))
            {
                throw new InvalidDataException($"生成任务 {task.Id} 的状态无效。");
            }

            if (task.CreatedAtUtc == default || task.UpdatedAtUtc < task.CreatedAtUtc)
            {
                throw new InvalidDataException($"生成任务 {task.Id} 的时间戳无效。");
            }

            if (task.RelativeAssetPaths is null)
            {
                throw new InvalidDataException($"生成任务 {task.Id} 的资产路径集合为空。");
            }

            var normalizedPaths = task.RelativeAssetPaths.Select(NormalizeRelativeAssetPath).ToArray();
            if (!normalizedPaths.SequenceEqual(task.RelativeAssetPaths, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"生成任务 {task.Id} 包含未规范化的资产路径。");
            }

            if (normalizedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedPaths.Length)
            {
                throw new InvalidDataException($"生成任务 {task.Id} 包含重复资产路径。");
            }

            ValidateState(task);
            ValidateDispatchMetadata(task);
            ValidateRetryMetadata(task);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            throw new InvalidDataException($"生成任务 {task.Id} 的持久化数据无效。", exception);
        }
    }

    private static void ValidateRetryMetadata(GenerationTask task)
    {
        if (task.RetryLineage is { } lineage)
        {
            if (lineage.SchemaVersion != GenerationTaskRetryLineage.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"生成任务 {task.Id} 的重试谱系版本无效：{lineage.SchemaVersion}。");
            }

            var rootTaskId = NormalizeTaskId(lineage.RootTaskId);
            if (!string.Equals(rootTaskId, lineage.RootTaskId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"生成任务 {task.Id} 的重试根任务 ID 未规范化。");
            }

            ValidateMaxAttempts(lineage.MaxAttempts, nameof(lineage.MaxAttempts));
            if (lineage.AttemptNumber < 1 || lineage.AttemptNumber > lineage.MaxAttempts)
            {
                throw new InvalidDataException($"生成任务 {task.Id} 的重试次数无效。");
            }

            if (lineage.AttemptNumber == 1)
            {
                if (lineage.PreviousTaskId is not null ||
                    !string.Equals(lineage.RootTaskId, task.Id, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"生成任务 {task.Id} 的首次尝试谱系无效。");
                }
            }
            else
            {
                if (lineage.PreviousTaskId is null ||
                    !string.Equals(
                        NormalizeTaskId(lineage.PreviousTaskId),
                        lineage.PreviousTaskId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"生成任务 {task.Id} 的上一尝试 ID 无效。");
                }
            }
        }

        if (task.SupersededByTaskId is { } supersededByTaskId)
        {
            if (task.Status != GenerationTaskStatus.Failed || task.RetryLineage is null)
            {
                throw new InvalidDataException(
                    $"只有带重试谱系的失败任务 {task.Id} 才能登记后继任务。");
            }

            if (!string.Equals(
                    NormalizeTaskId(supersededByTaskId),
                    supersededByTaskId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException($"生成任务 {task.Id} 的后继任务 ID 未规范化。");
            }
        }
    }

    private static void ValidateDispatchMetadata(GenerationTask task)
    {
        if (task.DispatchIntent is { } intent &&
            intent.SchemaVersion != GenerationTaskDispatchIntent.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"生成任务 {task.Id} 的调度意图版本无效：{intent.SchemaVersion}。");
        }

        if (task.AutomaticRetryBlockedReason is not { } blockedReason)
        {
            return;
        }

        ValidateText(
            blockedReason,
            nameof(task.AutomaticRetryBlockedReason),
            1,
            MaximumAutomaticRetryBlockedReasonLength);
        if (task.Status != GenerationTaskStatus.Failed ||
            task.FailureRetryable != true ||
            task.RetryLineage is not { } lineage ||
            lineage.AttemptNumber >= lineage.MaxAttempts ||
            task.SupersededByTaskId is not null)
        {
            throw new InvalidDataException(
                $"只有仍有额度且尚未被接替的可恢复失败任务 {task.Id} 才能记录自动重做阻断原因。");
        }
    }

    private static void ValidateMaxAttempts(int value, string parameterName)
    {
        if (value is < 1 or > MaximumAttempts)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"生成尝试总次数必须位于 1 到 {MaximumAttempts} 之间。");
        }
    }

    private static void ValidateOutput(GenerationOutputSpec output)
    {
        if (output is null)
        {
            throw new ArgumentException("输出规格不能为空。", nameof(output));
        }

        if (output.Width is < 1 or > 16_384 || output.Height is < 1 or > 16_384)
        {
            throw new ArgumentException("输出宽高必须位于 1 到 16384 像素之间。", nameof(output));
        }

        if (output.Count is < 1 or > MaximumOutputCount)
        {
            throw new ArgumentException(
                $"单任务输出数量必须位于 1 到 {MaximumOutputCount} 之间。",
                nameof(output));
        }

        if (output.MaxParallelism is < 1 or > MaximumOutputCount ||
            output.MaxParallelism > output.Count)
        {
            throw new ArgumentException(
                $"输出并行度必须位于 1 到 {output.Count} 之间。",
                nameof(output));
        }

        ValidateToken(output.Format, nameof(output.Format), 16);

        if (output.BackgroundRemoval is not null)
        {
            ValidateBackgroundRemoval(output.BackgroundRemoval, output);
        }
    }

    private static void ValidateBackgroundRemoval(
        GenerationBackgroundRemovalSpec backgroundRemoval,
        GenerationOutputSpec output)
    {
        if (backgroundRemoval.SchemaVersion != GenerationBackgroundRemovalSpec.CurrentSchemaVersion)
        {
            throw new ArgumentException(
                $"不支持的背景剔除约束版本：{backgroundRemoval.SchemaVersion}。",
                nameof(output));
        }

        if (!string.Equals(
                backgroundRemoval.Method,
                GenerationBackgroundRemovalSpec.BorderConnectedChromaKeyMethod,
                StringComparison.Ordinal))
        {
            throw new ArgumentException($"不支持的背景剔除方法：{backgroundRemoval.Method}。", nameof(output));
        }

        if (!string.Equals(output.Format.Trim().TrimStart('.'), "png", StringComparison.OrdinalIgnoreCase) ||
            !output.TransparentBackground)
        {
            throw new ArgumentException("启用背景剔除时，最终输出必须是透明 PNG。", nameof(output));
        }

        if (!Enum.TryParse<BackgroundKeyPreset>(backgroundRemoval.Preset, true, out var preset) ||
            !Enum.IsDefined(preset))
        {
            throw new ArgumentException($"不支持的背景底色预设：{backgroundRemoval.Preset}。", nameof(output));
        }

        if (!string.Equals(
                backgroundRemoval.Preset,
                preset.ToString().ToLowerInvariant(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException("背景底色预设必须使用规范的小写名称。", nameof(output));
        }

        var normalizedColor = BackgroundKeyColor.NormalizeHex(backgroundRemoval.ColorHex);
        if (!string.Equals(normalizedColor, backgroundRemoval.ColorHex, StringComparison.Ordinal))
        {
            throw new ArgumentException("背景剔除颜色必须是规范的大写 #RRGGBB。", nameof(output));
        }

        var expectedPresetColor = BackgroundKeyColor.ResolveHex(
            preset,
            preset == BackgroundKeyPreset.Custom ? normalizedColor : null);
        if (preset != BackgroundKeyPreset.Custom &&
            !string.Equals(expectedPresetColor, normalizedColor, StringComparison.Ordinal))
        {
            throw new ArgumentException("背景底色预设与冻结颜色不一致。", nameof(output));
        }

        if (!double.IsFinite(backgroundRemoval.TransparentThreshold) ||
            !double.IsFinite(backgroundRemoval.OpaqueThreshold) ||
            backgroundRemoval.TransparentThreshold is < 0d or > 441.7d ||
            backgroundRemoval.OpaqueThreshold is <= 0d or > 441.7d ||
            backgroundRemoval.OpaqueThreshold <= backgroundRemoval.TransparentThreshold)
        {
            throw new ArgumentException("背景剔除阈值无效；不透明阈值必须大于透明阈值。", nameof(output));
        }

        if (backgroundRemoval.EdgeContract is < 0 or > 3)
        {
            throw new ArgumentException("背景剔除边缘收缩必须位于 0 到 3 像素之间。", nameof(output));
        }

        if (backgroundRemoval.EdgeFeather is < 0 or > 2)
        {
            throw new ArgumentException("背景剔除边缘羽化必须位于 0 到 2 像素之间。", nameof(output));
        }

        var decodedRgbaBytes = checked(
            (long)output.Width *
            output.Height *
            GenerationBackgroundRemovalSpec.DecodedRgbaBytesPerPixel);
        if (decodedRgbaBytes > GenerationBackgroundRemovalSpec.MaximumDecodedRgbaBytes)
        {
            throw new ArgumentException(
                $"背景剔除单张图像解码预算不能超过 " +
                $"{GenerationBackgroundRemovalSpec.MaximumDecodedRgbaBytes / (1024L * 1024L)} MiB；" +
                $"当前 {output.Width}×{output.Height} RGBA 图像需要 {decodedRgbaBytes} 字节。",
                nameof(output));
        }
    }

    private static void ValidateState(GenerationTask task)
    {
        var hasClaim = task.ClaimedBy is not null && task.ClaimedAtUtc is not null;
        if (task.ClaimedBy is not null)
        {
            ValidateAgentId(task.ClaimedBy);
        }

        if (task.ClaimedAtUtc is { } claimedAt &&
            (claimedAt < task.CreatedAtUtc || task.UpdatedAtUtc < claimedAt))
        {
            throw new InvalidDataException($"生成任务 {task.Id} 的认领时间无效。");
        }

        switch (task.Status)
        {
            case GenerationTaskStatus.Pending when
                task.ClaimedBy is not null || task.ClaimedAtUtc is not null ||
                task.CompletedAtUtc is not null || task.FailedAtUtc is not null ||
                task.RelativeAssetPaths.Length != 0 || task.FailureMessage is not null:
                throw new InvalidDataException($"待处理任务 {task.Id} 包含不允许的状态数据。");

            case GenerationTaskStatus.Claimed when
                !hasClaim || task.CompletedAtUtc is not null || task.FailedAtUtc is not null ||
                task.RelativeAssetPaths.Length != 0 || task.FailureMessage is not null:
                throw new InvalidDataException($"已认领任务 {task.Id} 的状态数据不完整。");

            case GenerationTaskStatus.Completed when
                !hasClaim || task.CompletedAtUtc is null || task.FailedAtUtc is not null ||
                task.RelativeAssetPaths.Length == 0 || task.FailureMessage is not null:
                throw new InvalidDataException($"已完成任务 {task.Id} 的状态数据不完整。");

            case GenerationTaskStatus.Failed when
                !hasClaim || task.FailedAtUtc is null || task.CompletedAtUtc is not null ||
                task.RelativeAssetPaths.Length != 0 || string.IsNullOrWhiteSpace(task.FailureMessage):
                throw new InvalidDataException($"失败任务 {task.Id} 的状态数据不完整。");
        }

        if (task.FailureMessage is not null)
        {
            ValidateFailureMessage(task.FailureMessage);
        }

        if (task.FailureCode is not null)
        {
            ValidateFailureCode(task.FailureCode);
        }

        if (task.FailureStage is not null)
        {
            ValidateFailureStage(task.FailureStage);
        }

        if (task.FailureSubCode is not null)
        {
            ValidateFailureCode(task.FailureSubCode);
        }

        var hasStructuredFailure = task.FailureCode is not null ||
                                   task.FailureSubCode is not null ||
                                   task.FailureStage is not null ||
                                   task.FailureRetryable is not null;
        if (hasStructuredFailure &&
            (task.Status != GenerationTaskStatus.Failed ||
             task.FailureCode is null ||
             task.FailureStage is null ||
             task.FailureRetryable is null))
        {
            throw new InvalidDataException(
                $"生成任务 {task.Id} 的结构化失败信息必须完整且只能出现在 Failed 状态。");
        }

        var terminalAt = task.CompletedAtUtc ?? task.FailedAtUtc;
        if (terminalAt is { } value && (value < task.ClaimedAtUtc || task.UpdatedAtUtc < value))
        {
            throw new InvalidDataException($"生成任务 {task.Id} 的终态时间无效。");
        }
    }

    private static void ValidateBatchMetadata(GenerationTask task)
    {
        var hasBatchId = task.BatchId is not null;
        var hasItemId = task.BatchItemId is not null;
        var hasItemIndex = task.BatchItemIndex is not null;
        if (!hasBatchId && !hasItemId && !hasItemIndex)
        {
            return;
        }

        if (!hasBatchId || !hasItemId || !hasItemIndex)
        {
            throw new InvalidDataException(
                $"生成任务 {task.Id} 的批次元数据必须同时包含 BatchId、BatchItemId 和 BatchItemIndex。");
        }

        try
        {
            ValidateCanonicalGuid(task.BatchId!, nameof(task.BatchId), "批次 ID");
            ValidateCanonicalGuid(task.BatchItemId!, nameof(task.BatchItemId), "批次项目 ID");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"生成任务 {task.Id} 的批次元数据无效。", exception);
        }

        if (task.BatchItemIndex < 0 || task.BatchItemIndex >= MaximumBatchItemCount)
        {
            throw new InvalidDataException($"生成任务 {task.Id} 的批次项目索引无效。");
        }
    }

    private static void ValidateCanonicalGuid(string value, string parameterName, string displayName)
    {
        if (!Guid.TryParseExact(value, "N", out var parsed) ||
            !string.Equals(value, parsed.ToString("N"), StringComparison.Ordinal))
        {
            throw new ArgumentException($"{displayName} 必须是规范的 32 位 GUID。", parameterName);
        }
    }

    private static void ValidateToken(
        string value,
        string parameterName,
        int maximumLength,
        bool allowSlash = false,
        bool allowColon = false)
    {
        ValidateText(value, parameterName, 1, maximumLength);

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character is '.' or '_' or '-' ||
                (allowSlash && character == '/') ||
                (allowColon && character == ':'))
            {
                continue;
            }

            throw new ArgumentException($"{parameterName} 包含不允许的字符。", parameterName);
        }
    }

    private static void ValidateText(string value, string parameterName, int minimumLength, int maximumLength)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (value.Length < minimumLength || value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(character => character is '\0' || char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException($"{parameterName} 长度、首尾空白或控制字符无效。", parameterName);
        }
    }

    private static void ValidateFailureToken(string value, string parameterName)
    {
        ValidateText(value, parameterName, 1, 128);
        if (value.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException($"{parameterName} 包含不允许的字符。", parameterName);
        }
    }
}
