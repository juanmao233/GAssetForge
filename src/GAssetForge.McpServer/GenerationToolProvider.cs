using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GAssetForge.Core;
using GAssetForge.Core.Generation;

namespace GAssetForge.McpServer;

internal sealed class GenerationToolProvider(
    IGenerationTaskStore taskStore,
    WorkspaceContextResolver contextResolver,
    ServerOptions options,
    string storeFilePath,
    IImageGenerationCliFallbackService? imageGenerationCliFallbackService = null)
{
    private readonly GeneratedAssetPostProcessor _postProcessor = new(contextResolver);
    private readonly GeneratedOutputSubmissionService _submissionService =
        new(contextResolver, new GeneratedAssetPostProcessor(contextResolver));
    private readonly IImageGenerationCliFallbackService _imageGenerationCliFallbackService =
        imageGenerationCliFallbackService ?? new ImageGenerationCliFallbackService();

    public McpToolCatalog CreateCatalog() => new(CreateDefinitions());

    private IReadOnlyList<McpToolDefinition> CreateDefinitions() =>
    [
        new(
            "server_info",
            "返回 GAssetForge MCP 版本、当前传输方式、数据目录、任务存储位置与当前有效资产根目录。",
            McpToolCatalog.Schema("""
                {"type":"object","properties":{},"additionalProperties":false}
                """),
            ServerInfoAsync),
        new(
            "get_current_context",
            "读取桌面端当前业务工作流、项目资产目录和 Tilemap 参数；Tilemap 工作流同时通过 Core 构建器返回可直接用于 AI 绘图任务的正式约束包。建议提供 contentPrompt。",
            McpToolCatalog.Schema("""
                {
                  "type":"object",
                  "properties":{
                    "contentPrompt":{"type":"string","description":"具体要生成的瓦片内容；省略时生成明确的待补充占位提示"},
                    "stylePrompt":{"type":"string","description":"可选的美术风格描述"},
                    "negativePrompt":{"type":"string","description":"可选的负向提示词"}
                  },
                  "additionalProperties":false
                }
                """),
            GetCurrentContextAsync),
        new(
            "create_generation_task",
            "创建一个持久化的 AI 美术生成任务。constraints 应优先采用 get_current_context 返回的 generationConstraints，再补充具体美术提示词。",
            McpToolCatalog.Schema("""
                {
                  "type":"object",
                  "properties":{
                    "displayName":{"type":"string","minLength":1,"maxLength":256,"description":"任务显示名称"},
                    "requestedBy":{"type":"string","minLength":1,"maxLength":128,"description":"创建者或 Agent 标识"},
                    "constraints":{
                      "type":"object",
                      "properties":{
                        "schemaVersion":{"type":"integer","const":1,"default":1},
                        "workflowId":{"type":"string","minLength":1},
                        "preferredSkill":{"type":"string"},
                        "prompt":{"type":"string","minLength":1},
                        "negativePrompt":{"type":"string"},
                        "output":{
                          "type":"object",
                          "properties":{
                             "width":{"type":"integer","minimum":1,"maximum":16384},
                             "height":{"type":"integer","minimum":1,"maximum":16384},
                             "count":{"type":"integer","minimum":1,"maximum":16},
                             "maxParallelism":{"type":"integer","minimum":1,"maximum":16,"default":1,"description":"该任务允许同时生成的最大图片数；不得超过 count"},
                             "format":{"type":"string"},
                            "transparentBackground":{"type":"boolean"},
                            "backgroundRemoval":{
                              "type":["object","null"],
                              "properties":{
                                "schemaVersion":{"type":"integer","const":1,"default":1},
                                "method":{"type":"string","const":"border-connected-chroma-key"},
                                "preset":{"type":"string","enum":["magenta","green","blue","black","white","custom"]},
                                "colorHex":{"type":"string","pattern":"^#[0-9A-F]{6}$"},
                                "transparentThreshold":{"type":"number","minimum":0,"maximum":441.7},
                                "opaqueThreshold":{"type":"number","exclusiveMinimum":0,"maximum":441.7},
                                "edgeContract":{"type":"integer","minimum":0,"maximum":3},
                                "edgeFeather":{"type":"integer","minimum":0,"maximum":2},
                                "despill":{"type":"boolean"},
                                "edgeConnectedOnly":{"type":"boolean"}
                              },
                              "required":["schemaVersion","method","preset","colorHex","transparentThreshold","opaqueThreshold","edgeContract","edgeFeather","despill","edgeConnectedOnly"],
                              "additionalProperties":false
                            }
                          },
                          "required":["width","height","count","format","transparentBackground"],
                          "additionalProperties":false
                        },
                        "rules":{
                          "type":"array",
                          "maxItems":128,
                          "items":{
                            "type":"object",
                            "properties":{
                              "key":{"type":"string","minLength":1},
                              "category":{"type":"string","minLength":1},
                              "instruction":{"type":"string","minLength":1},
                              "required":{"type":"boolean"},
                              "weight":{"type":["number","null"],"minimum":0,"maximum":1}
                            },
                            "required":["key","category","instruction","required"],
                            "additionalProperties":false
                          }
                        },
                        "parameters":{"type":"object"}
                      },
                      "required":["workflowId","prompt","output","rules","parameters"],
                      "additionalProperties":false
                    }
                  },
                  "required":["displayName","constraints"],
                  "additionalProperties":false
                }
                """),
            CreateTaskAsync),
        new(
            "list_generation_tasks",
            "列出 AI 美术生成任务，可按状态和业务工作流筛选。",
            McpToolCatalog.Schema("""
                {
                  "type":"object",
                  "properties":{
                    "status":{"type":"string","enum":["pending","claimed","completed","failed"]},
                    "workflowId":{"type":"string"},
                    "maxResults":{"type":"integer","minimum":1,"maximum":500,"default":100}
                  },
                  "additionalProperties":false
                }
                """),
            ListTasksAsync),
        new(
            "get_generation_task",
            "按任务 ID 获取一个 AI 美术生成任务。",
            McpToolCatalog.Schema("""
                {
                  "type":"object",
                  "properties":{"taskId":{"type":"string","minLength":1}},
                  "required":["taskId"],
                  "additionalProperties":false
                }
                """),
            GetTaskAsync),
        new(
            "claim_generation_task",
            "认领指定任务；省略 taskId 时按创建顺序认领下一个待处理任务，可用 workflowId 筛选。",
            McpToolCatalog.Schema("""
                {
                  "type":"object",
                  "properties":{
                    "agentId":{"type":"string","minLength":1,"maxLength":128},
                    "taskId":{"type":"string","minLength":1},
                    "workflowId":{"type":"string"}
                  },
                  "required":["agentId"],
                  "additionalProperties":false
                }
                """),
            ClaimTaskAsync),
        new(
            "submit_generation_output",
            "为带 delivery.workspace 的单图任务提交真实图像模型返回文件。GAssetForge 会把 sourcePath 原图归档到当前项目，软件完成尺寸/纯色底归一化、背景剔除、约束验证、最终原子提交和任务完成登记；Agent 不需要创建目录、搬运、缩放、抠图或再次调用 complete。",
            McpToolCatalog.Schema("""
                {
                  "type":"object",
                  "properties":{
                    "taskId":{"type":"string","minLength":1},
                    "agentId":{"type":"string","minLength":1,"maxLength":128},
                    "outputIndex":{"type":"integer","minimum":0,"default":0},
                    "sourcePath":{"type":"string","minLength":1,"description":"真实图像模型返回的绝对本地 PNG 路径"}
                  },
                  "required":["taskId","agentId","sourcePath"],
                  "additionalProperties":false
                }
                """),
            SubmitOutputAsync),
        new(
            "run_image_delivery_fallback",
            "仅当本次内置 ImageGen 返回可视结果但 savedPath 为空时，调用 GAssetForge Harness 的官方 image_gen.py 本地交付兜底。软件从已认领任务读取冻结提示、参考图、尺寸和 recommendedSourcePath，自行发现 Python、执行模型 CLI、验证 PNG、完成后处理并提交；Agent 不得传入任意命令、路径或提示词。",
            McpToolCatalog.Schema("""
                {
                  "type":"object",
                  "properties":{
                    "taskId":{"type":"string","minLength":1},
                    "agentId":{"type":"string","minLength":1,"maxLength":128}
                  },
                  "required":["taskId","agentId"],
                  "additionalProperties":false
                }
                """),
            RunImageDeliveryFallbackAsync),
        new(
            "process_generation_output",
            "把已认领任务中指定 0-based outputIndex 的纯色底 staging PNG 交给 GAssetForge 本地剔色，验证最终尺寸、Alpha 与工作流约束后再原子提交到冻结的 targetPath。路径只能来自 parameters.delivery。",
            McpToolCatalog.Schema("""
                {
                  "type":"object",
                  "properties":{
                    "taskId":{"type":"string","minLength":1},
                    "agentId":{"type":"string","minLength":1,"maxLength":128},
                    "outputIndex":{"type":"integer","minimum":0}
                  },
                  "required":["taskId","agentId","outputIndex"],
                  "additionalProperties":false
                }
                """),
            ProcessOutputAsync),
        new(
            "complete_generation_task",
            "完成已认领任务。带 delivery 的任务必须把 assetPaths 精确写入冻结的最终 targetPaths；启用背景剔除时会兜底处理尚未提交但仍存在的 staging PNG，然后验证真实文件、数量、格式、PNG 尺寸、Alpha 与工作流约束。不得用 staging 路径完成登记。",
            McpToolCatalog.Schema("""
                {
                  "type":"object",
                  "properties":{
                    "taskId":{"type":"string","minLength":1},
                    "agentId":{"type":"string","minLength":1,"maxLength":128},
                    "assetPaths":{"type":"array","minItems":1,"items":{"type":"string","minLength":1}}
                  },
                  "required":["taskId","agentId","assetPaths"],
                  "additionalProperties":false
                }
                """),
            CompleteTaskAsync),
        new(
            "fail_generation_task",
            "把已认领任务标记为失败并记录可操作的失败原因。",
            McpToolCatalog.Schema("""
                {
                  "type":"object",
                  "properties":{
                    "taskId":{"type":"string","minLength":1},
                    "agentId":{"type":"string","minLength":1,"maxLength":128},
                    "failureMessage":{"type":"string","minLength":1}
                  },
                  "required":["taskId","agentId","failureMessage"],
                  "additionalProperties":false
                }
                """),
            FailTaskAsync)
    ];

    private Task<object?> ServerInfoAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var context = contextResolver.Resolve();
        return Task.FromResult<object?>(new
        {
            name = "GAssetForge",
            version = ProductIdentity.GetAssemblyInformationalVersion(typeof(GenerationToolProvider).Assembly),
            transport = options.UsesHttp
                ? "streamable-http-jsonrpc-2.0"
                : "stdio-jsonrpc-2.0-line-delimited",
            endpoint = options.UsesHttp ? options.Endpoint : null,
            expectedAgent = options.UsesHttp ? options.ExpectedAgent : null,
            supportedProtocolVersions = new[] { "2024-11-05", "2025-03-26", "2025-06-18" },
            dataRoot = options.DataDirectory,
            generationStore = storeFilePath,
            assets = context.Assets,
            safety = new
            {
                completionRequiresExistingRegularFiles = true,
                completionUsesDeliveryAssetRootSnapshot = true,
                completionRequiresExactDeliveryTargets = true,
                reparsePointsRejected = true
            }
        });
    }

    private Task<object?> GetCurrentContextAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var context = contextResolver.Resolve();
        var contentPrompt = JsonArguments.OptionalString(arguments, "contentPrompt");
        var stylePrompt = JsonArguments.OptionalString(arguments, "stylePrompt");
        var negativePrompt = JsonArguments.OptionalString(arguments, "negativePrompt");
        var constraintResult = BuildCurrentGenerationConstraints(
            context,
            contentPrompt ?? "待补充具体内容的单瓦片美术资产",
            stylePrompt,
            negativePrompt);
        return Task.FromResult<object?>(new
        {
            activeWorkflow = context.ActiveWorkflow,
            assets = context.Assets,
            tilemap = context.Tilemap,
            generationConstraints = constraintResult?.Constraints,
            generationMetrics = constraintResult?.Metrics,
            settings = new
            {
                exists = context.SettingsFileExists,
                path = context.SettingsFilePath
            },
            guidance = constraintResult is null
                ? "当前工作流尚未提供专用约束构建器，可自行创建 provider-neutral 约束包。"
                : contentPrompt is null
                    ? "当前约束包使用占位内容；请带 contentPrompt 再调用本工具，或在创建任务前替换 constraints.prompt。"
                    : "generationConstraints 已包含当前 Tilemap 几何、画布、透明区域和具体内容要求，可直接用于 create_generation_task.constraints。"
        });
    }

    private async Task<object?> CreateTaskAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<CreateGenerationTaskRequest>(
            arguments.GetRawText(),
            GenerationJson.Options)
            ?? throw new JsonException("生成任务请求不能为空。");
        var delivery = ReadDeliveryOrThrow(request.Constraints);
        if (delivery is not null)
        {
            contextResolver.ValidateDeliveryAssetRootForCreation(delivery.AssetRoot);
        }

        var task = await taskStore.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        return new { task };
    }

    private async Task<object?> ListTasksAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var statusText = JsonArguments.OptionalString(arguments, "status");
        GenerationTaskStatus? status = null;
        if (statusText is not null)
        {
            if (!Enum.TryParse<GenerationTaskStatus>(statusText, ignoreCase: true, out var parsedStatus) ||
                !Enum.IsDefined(parsedStatus))
            {
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"未知任务状态：{statusText}");
            }

            status = parsedStatus;
        }

        var workflowId = JsonArguments.OptionalString(arguments, "workflowId");
        var maxResults = ReadOptionalInt32(arguments, "maxResults", 100, 1, 500);
        var allTasks = await taskStore.ListAsync(status, workflowId, cancellationToken).ConfigureAwait(false);
        var tasks = allTasks.Take(maxResults).ToArray();
        return new
        {
            tasks,
            returnedCount = tasks.Length,
            totalMatchedCount = allTasks.Count,
            truncated = tasks.Length < allTasks.Count
        };
    }

    private async Task<object?> GetTaskAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var taskId = JsonArguments.RequiredString(arguments, "taskId");
        var task = await taskStore.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        return new { found = task is not null, task };
    }

    private async Task<object?> ClaimTaskAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var agentId = JsonArguments.RequiredString(arguments, "agentId");
        var taskId = JsonArguments.OptionalString(arguments, "taskId");
        var workflowId = JsonArguments.OptionalString(arguments, "workflowId");
        if (taskId is not null && workflowId is not null)
        {
            throw new JsonRpcException(
                JsonRpcErrorCodes.InvalidParams,
                "指定 taskId 时不能同时提供 workflowId；workflowId 只用于认领下一个任务。");
        }

        GenerationTask? task;
        GenerationDeliveryPlan? delivery = null;
        string? recommendedSourcePath = null;
        object? imageGeneration = null;
        if (taskId is null)
        {
            var pendingTasks = await taskStore.ListAsync(
                    GenerationTaskStatus.Pending,
                    workflowId,
                    cancellationToken)
                .ConfigureAwait(false);
            var candidate = pendingTasks.FirstOrDefault();
            if (candidate is null)
            {
                task = null;
            }
            else
            {
                delivery = ReadDeliveryOrThrow(candidate.Constraints);
                if (delivery?.Workspace is not null)
                {
                    recommendedSourcePath = ResolveRecommendedSourcePath(delivery);
                    imageGeneration = await BuildImageGenerationContractAsync(candidate, cancellationToken)
                        .ConfigureAwait(false);
                }

                task = await taskStore.ClaimAsync(candidate.Id, agentId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            var candidate = await taskStore.GetAsync(taskId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"生成任务不存在：{taskId}");
            delivery = ReadDeliveryOrThrow(candidate.Constraints);
            if (delivery?.Workspace is not null)
            {
                recommendedSourcePath = ResolveRecommendedSourcePath(delivery);
                imageGeneration = await BuildImageGenerationContractAsync(candidate, cancellationToken)
                    .ConfigureAwait(false);
            }

            task = await taskStore.ClaimAsync(taskId, agentId, cancellationToken).ConfigureAwait(false);
        }

        if (task is not null)
        {
            GenerationRunJournal.Append(
                delivery,
                new GenerationRunStageEvent
                {
                    BatchId = task.BatchId,
                    TaskId = task.Id,
                    Stage = "task.claim",
                    Status = "completed",
                    StartedAtUtc = task.CreatedAtUtc,
                    EndedAtUtc = task.ClaimedAtUtc,
                    ElapsedMilliseconds = task.ClaimedAtUtc is { } claimedAt
                        ? Math.Max(0L, (long)(claimedAt - task.CreatedAtUtc).TotalMilliseconds)
                        : null,
                    AgentId = task.ClaimedBy,
                    Message = "任务已通过 MCP 认领。"
                });
        }

        var currentAssetRoot = contextResolver.Resolve().Assets.EffectivePath;
        return new
        {
            claimed = task is not null,
            task,
            dataRoot = options.DataDirectory,
            delivery,
            assetRoot = delivery?.AssetRoot ?? currentAssetRoot,
            recommendedSourcePath,
            imageGeneration
        };
    }

    private async Task<object> BuildImageGenerationContractAsync(
        GenerationTask task,
        CancellationToken cancellationToken)
    {
        var output = task.Constraints.Output;
        var targetDivisor = GreatestCommonDivisor(output.Width, output.Height);
        var cliSize = ResolveCliSize(output.Width, output.Height);
        var styleNode = task.Constraints.Parameters?["style"] as JsonObject;
        var referenceSourcePaths = await ResolveStyleReferenceSourcePathsAsync(
                styleNode,
                cancellationToken)
            .ConfigureAwait(false);
        return new
        {
            prompt = task.Constraints.Prompt,
            negativePrompt = task.Constraints.NegativePrompt,
            requiredRules = task.Constraints.Rules
                .Where(rule => rule.Required)
                .Select(rule => new { rule.Key, rule.Category, rule.Instruction })
                .ToArray(),
            output = new
            {
                targetWidth = output.Width,
                targetHeight = output.Height,
                targetAspectRatio = $"{output.Width / targetDivisor}:{output.Height / targetDivisor}",
                cliSize = cliSize.Size,
                preservesExactAspectRatio = cliSize.PreservesExactAspectRatio,
                format = output.Format,
                output.BackgroundRemoval
            },
            style = new
            {
                groupId = ReadOptionalString(styleNode, "groupId"),
                name = ReadOptionalString(styleNode, "name"),
                description = ReadOptionalString(styleNode, "description"),
                prompt = ReadOptionalString(styleNode, "prompt"),
                referenceSourcePaths
            }
        };
    }

    private async Task<string[]> ResolveStyleReferenceSourcePathsAsync(
        JsonObject? styleNode,
        CancellationToken cancellationToken)
    {
        if (styleNode?["references"] is not JsonArray references || references.Count == 0)
        {
            return [];
        }

        var dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DataDirectory));
        var results = new string[references.Count];
        for (var index = 0; index < references.Count; index++)
        {
            if (references[index] is not JsonObject referenceNode)
            {
                throw new InvalidDataException("风格参考图合同包含无效对象。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = GenerationInputValidator.NormalizeRelativeAssetPath(
                ReadRequiredString(referenceNode, "dataRootRelativePath", "风格参考图缺少 dataRootRelativePath。"));
            var expectedSha256 = ReadRequiredString(
                    referenceNode,
                    "sha256",
                    "风格参考图缺少冻结 SHA-256。")
                .Trim();
            if (expectedSha256.Length != 64 ||
                expectedSha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException($"风格参考图冻结 SHA-256 无效：{relativePath}");
            }

            var fullPath = Path.GetFullPath(Path.Combine(
                dataRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var relativeToRoot = Path.GetRelativePath(dataRoot, fullPath);
            if (Path.IsPathRooted(relativeToRoot) ||
                relativeToRoot is "." or ".." ||
                relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relativeToRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
                !File.Exists(fullPath))
            {
                throw new InvalidDataException($"风格参考图快照不存在或越出数据根：{relativePath}");
            }

            EnsureStyleReferencePathHasNoReparsePoints(dataRoot, fullPath, relativePath);
            var actualSha256 = await ComputeSha256Async(fullPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"风格参考图快照内容与冻结 SHA-256 不一致：{relativePath}");
            }

            results[index] = fullPath;
        }

        return results;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 128 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                var bytesRead = await stream.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, bytesRead));
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnsureStyleReferencePathHasNoReparsePoints(
        string dataRoot,
        string fullPath,
        string relativePath)
    {
        if (!Directory.Exists(dataRoot))
        {
            throw new InvalidDataException($"风格参考图数据根不存在：{dataRoot}");
        }

        if ((File.GetAttributes(dataRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("风格参考图数据根不能是符号链接或目录联接。");
        }

        var relativeToRoot = Path.GetRelativePath(dataRoot, fullPath);
        var segments = relativeToRoot.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var current = dataRoot;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"风格参考图快照路径不能经过符号链接或目录联接：{relativePath}");
            }
        }
    }

    private static (string Size, bool PreservesExactAspectRatio) ResolveCliSize(int width, int height)
    {
        const long minimumPixels = 655_360L;
        const long maximumPixels = 8_294_400L;
        const int maximumEdge = 3_840;
        var divisor = GreatestCommonDivisor(width, height);
        var baseWidth = width / divisor;
        var baseHeight = height / divisor;
        if ((long)Math.Max(baseWidth, baseHeight) > 3L * Math.Min(baseWidth, baseHeight))
        {
            return width >= height ? ("1440x480", false) : ("480x1440", false);
        }

        var widthStep = 16 / GreatestCommonDivisor(baseWidth, 16);
        var heightStep = 16 / GreatestCommonDivisor(baseHeight, 16);
        var multiplierStep = LeastCommonMultiple(widthStep, heightStep);
        var minimumMultiplier = (int)Math.Ceiling(Math.Sqrt(
            minimumPixels / ((double)baseWidth * baseHeight)));
        var multiplier = Math.Max(
            multiplierStep,
            ((minimumMultiplier + multiplierStep - 1) / multiplierStep) * multiplierStep);
        var candidateWidth = (long)baseWidth * multiplier;
        var candidateHeight = (long)baseHeight * multiplier;
        var candidatePixels = candidateWidth * candidateHeight;
        if (candidateWidth <= maximumEdge &&
            candidateHeight <= maximumEdge &&
            candidatePixels is >= minimumPixels and <= maximumPixels)
        {
            return ($"{candidateWidth}x{candidateHeight}", true);
        }

        return width == height
            ? ("1024x1024", false)
            : width > height
                ? ("1536x1024", false)
                : ("1024x1536", false);
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        left = Math.Abs(left);
        right = Math.Abs(right);
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Max(1, left);
    }

    private static int LeastCommonMultiple(int left, int right) =>
        checked(left / GreatestCommonDivisor(left, right) * right);

    private static string ReadOptionalString(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result ?? string.Empty
            : string.Empty;

    private static string ReadRequiredString(JsonObject node, string name, string error) =>
        node[name] is JsonValue value &&
        value.TryGetValue<string>(out var result) &&
        !string.IsNullOrWhiteSpace(result)
            ? result
            : throw new InvalidDataException(error);

    private string ResolveRecommendedSourcePath(GenerationDeliveryPlan delivery)
    {
        var workspace = delivery.Workspace
            ?? throw new InvalidDataException("推荐模型源路径要求 generation workspace。");
        if (workspace.ModelOutputPaths.Length != 1)
        {
            throw new InvalidDataException(
                "新生成任务必须为单图 workspace，才能返回唯一 recommendedSourcePath。");
        }

        return contextResolver.ResolveDeliveryAssetPath(
                workspace.ModelOutputPaths[0],
                delivery.AssetRoot,
                requireExistingFile: false,
                "推荐模型源文件")
            .FullPath;
    }

    private async Task<object?> CompleteTaskAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var taskId = JsonArguments.RequiredString(arguments, "taskId");
        var agentId = JsonArguments.RequiredString(arguments, "agentId");
        var suppliedPaths = JsonArguments.RequiredStringArray(arguments, "assetPaths");
        var existingTask = await taskStore.GetAsync(taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"生成任务不存在：{taskId}");
        var delivery = ReadDeliveryOrThrow(existingTask.Constraints);
        var assetRoot = delivery?.AssetRoot ?? contextResolver.Resolve().Assets.EffectivePath;
        var plannedRelativePaths = contextResolver.ValidatePlannedAssetPaths(suppliedPaths, assetRoot);
        if (plannedRelativePaths.Count != existingTask.Constraints.Output.Count)
        {
            throw new InvalidDataException(
                $"任务要求 {existingTask.Constraints.Output.Count} 个产物，实际提供 {plannedRelativePaths.Count} 个。");
        }

        if (delivery is not null && !PathsMatchExactly(plannedRelativePaths, delivery.TargetPaths))
        {
            throw new InvalidDataException(
                "完成产物路径必须与任务 parameters.delivery.targetPaths 按顺序精确一致。");
        }

        var fallbackProcessing = new List<GeneratedOutputProcessingResult>();
        if (existingTask.Constraints.Output.BackgroundRemoval is not null)
        {
            if (delivery is null)
            {
                throw new InvalidDataException("启用背景剔除的任务必须包含 parameters.delivery。");
            }

            if (existingTask.Status != GenerationTaskStatus.Completed)
            {
                EnsureClaimOwnershipForProcessing(existingTask, agentId);
                for (var outputIndex = 0;
                     outputIndex < existingTask.Constraints.Output.Count;
                     outputIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = contextResolver.ResolveDeliveryAssetPath(
                        delivery.TargetPaths[outputIndex],
                        delivery.AssetRoot,
                        requireExistingFile: false,
                        "最终交付");
                    var staging = contextResolver.ResolveDeliveryAssetPath(
                        delivery.StagingPaths[outputIndex],
                        delivery.AssetRoot,
                        requireExistingFile: false,
                        "背景剔除 staging");
                    if (!File.Exists(staging.FullPath) ||
                        GeneratedAssetPostProcessor.IsValidFinalTarget(
                            target.FullPath,
                            existingTask.Constraints))
                    {
                        continue;
                    }

                    existingTask = await taskStore.RenewClaimAsync(taskId, agentId, cancellationToken)
                        .ConfigureAwait(false);
                    fallbackProcessing.Add(await _postProcessor.ProcessAsync(
                            existingTask,
                            delivery,
                            agentId,
                            outputIndex,
                            cancellationToken)
                        .ConfigureAwait(false));
                }
            }
        }

        var relativePaths = contextResolver.ValidateCompletedAssetPaths(suppliedPaths, assetRoot);

        EnsureOutputFormatsMatch(relativePaths, existingTask.Constraints.Output.Format);
        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.GetFullPath(Path.Combine(
                assetRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            GeneratedAssetValidator.Validate(fullPath, existingTask.Constraints);
        }

        var task = await taskStore.CompleteAsync(taskId, agentId, relativePaths, cancellationToken)
            .ConfigureAwait(false);
        GenerationRunJournal.Append(
            delivery,
            new GenerationRunStageEvent
            {
                BatchId = task.BatchId,
                TaskId = task.Id,
                Stage = "task.complete",
                Status = "completed",
                EndedAtUtc = task.CompletedAtUtc,
                AgentId = agentId,
                ObservedPath = string.Join(";", relativePaths),
                Message = "旧兼容流程已完成任务登记。"
            });
        return new
        {
            task,
            effectiveAssetRoot = assetRoot,
            delivery,
            relativeAssetPaths = relativePaths,
            fallbackProcessing
        };
    }

    private async Task<object?> ProcessOutputAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var taskId = JsonArguments.RequiredString(arguments, "taskId");
        var agentId = JsonArguments.RequiredString(arguments, "agentId");
        var outputIndex = ReadRequiredInt32(arguments, "outputIndex", 0, int.MaxValue);
        var existingTask = await taskStore.GetAsync(taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"生成任务不存在：{taskId}");
        EnsureClaimOwnershipForProcessing(existingTask, agentId);
        var task = await taskStore.RenewClaimAsync(existingTask.Id, agentId, cancellationToken)
            .ConfigureAwait(false);
        var delivery = ReadDeliveryOrThrow(task.Constraints);
        var result = await _postProcessor.ProcessAsync(
                task,
                delivery,
                agentId,
                outputIndex,
                cancellationToken)
            .ConfigureAwait(false);
        return new
        {
            taskId = task.Id,
            agentId,
            outputIndex = result.OutputIndex,
            stagingPath = result.RelativeStagingPath,
            processedPath = result.RelativeProcessedPath,
            targetPath = result.RelativeTargetPath,
            result.Processed,
            result.AlreadyFinal,
            result.Skipped,
            result.ReplacedExistingTarget,
            result.StagingDeleted,
            result.Message,
            statistics = result.Statistics
        };
    }

    private async Task<object?> SubmitOutputAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var taskId = JsonArguments.RequiredString(arguments, "taskId");
        var agentId = JsonArguments.RequiredString(arguments, "agentId");
        var sourcePath = JsonArguments.RequiredString(arguments, "sourcePath");
        var outputIndex = ReadOptionalInt32(arguments, "outputIndex", 0, 0, int.MaxValue);
        var existingTask = await taskStore.GetAsync(taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"生成任务不存在：{taskId}");
        var delivery = ReadDeliveryOrThrow(existingTask.Constraints)
            ?? throw new InvalidDataException("submit_generation_output 要求冻结 parameters.delivery。");
        return await SubmitOutputCoreAsync(
                existingTask,
                delivery,
                agentId,
                outputIndex,
                sourcePath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<object?> RunImageDeliveryFallbackAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var taskId = JsonArguments.RequiredString(arguments, "taskId");
        var agentId = JsonArguments.RequiredString(arguments, "agentId");
        var existingTask = await taskStore.GetAsync(taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"生成任务不存在：{taskId}");
        var delivery = ReadDeliveryOrThrow(existingTask.Constraints)
            ?? throw new InvalidDataException("图片本地交付兜底要求冻结 parameters.delivery。");
        if (delivery.Workspace is not { } workspace)
        {
            throw new InvalidDataException(
                "图片本地交付兜底只支持带 project-local workspace 的新单图任务。");
        }

        if (existingTask.Constraints.Output.Count != 1 ||
            workspace.ModelOutputPaths.Length != 1 ||
            delivery.TargetPaths.Length != 1)
        {
            throw new InvalidDataException(
                "图片本地交付兜底要求一张图片一个任务，并且只有一个冻结模型源路径和最终目标。");
        }

        if (existingTask.Status == GenerationTaskStatus.Completed)
        {
            return new
            {
                task = existingTask,
                idempotent = true,
                targetPath = delivery.TargetPaths[0],
                message = "任务已经完成；未重复调用 Image API。"
            };
        }

        EnsureClaimOwnershipForProcessing(existingTask, agentId);
        var task = await taskStore.RenewClaimAsync(existingTask.Id, agentId, cancellationToken)
            .ConfigureAwait(false);
        var recommendedSourcePath = ResolveRecommendedSourcePath(delivery);
        var promptPlan = contextResolver.ResolveDeliveryAssetPath(
            workspace.PromptPath,
            delivery.AssetRoot,
            requireExistingFile: false,
            "图片 CLI 冻结提示文件");
        var fallbackPromptPath = Path.Combine(
            Path.GetDirectoryName(promptPlan.FullPath)!,
            $"{Path.GetFileNameWithoutExtension(promptPlan.FullPath)}.imagegen-cli.txt");
        var referenceSourcePaths = await ResolveStyleReferenceSourcePathsAsync(
                task.Constraints.Parameters?["style"] as JsonObject,
                cancellationToken)
            .ConfigureAwait(false);
        var fallbackRequest = new ImageGenerationCliFallbackRequest(
            task.Id,
            agentId,
            BuildImageGenerationPrompt(task),
            fallbackPromptPath,
            referenceSourcePaths,
            ResolveCliSize(
                    task.Constraints.Output.Width,
                    task.Constraints.Output.Height)
                .Size,
            recommendedSourcePath);

        ImageGenerationCliFallbackResult fallback;
        using (var measurement = GenerationRunJournal.Measure(
                   delivery,
                   task,
                   "image.cli_fallback",
                   agentId,
                   0,
                   workspace.ModelOutputPaths[0],
                   recommendedSourcePath))
        {
            try
            {
                fallback = await _imageGenerationCliFallbackService.GenerateAsync(
                        fallbackRequest,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!PathsEqual(fallback.SourcePath, recommendedSourcePath))
                {
                    throw new InvalidDataException(
                        "图片 CLI 兜底返回路径与冻结 recommendedSourcePath 不一致。");
                }

                measurement.Complete(
                    fallback.AlreadyExisted
                        ? "发现精确 recommendedSourcePath 已有有效 PNG，跳过重复 Image API 调用。"
                        : "官方 image_gen.py 已把模型 PNG 直接写入冻结 recommendedSourcePath。",
                    workspace.ModelOutputPaths[0]);
            }
            catch (Exception exception)
            {
                measurement.Fail(exception, "image_delivery_fallback_failed");
                throw;
            }
        }

        var refreshedTask = await taskStore.GetAsync(task.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"生成任务不存在：{task.Id}");
        var submission = await SubmitOutputCoreAsync(
                refreshedTask,
                delivery,
                agentId,
                0,
                fallback.SourcePath,
                cancellationToken)
            .ConfigureAwait(false);
        return new
        {
            fallback = new
            {
                fallback.SourcePath,
                fallback.PythonCommandDisplay,
                fallback.ImageGenScriptPath,
                fallback.DiagnosticSummary,
                fallback.AlreadyExisted
            },
            submission
        };
    }

    private async Task<object> SubmitOutputCoreAsync(
        GenerationTask existingTask,
        GenerationDeliveryPlan delivery,
        string agentId,
        int outputIndex,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (delivery.Workspace is null)
        {
            throw new InvalidDataException(
                "该任务是没有 project-local workspace 的旧任务，请使用旧 process/complete 流程。");
        }

        if (existingTask.Constraints.Output.Count != 1 || outputIndex != 0)
        {
            throw new InvalidDataException(
                "新 submit_generation_output 合同要求一张图片一个任务，output.count=1 且 outputIndex=0。");
        }

        if (existingTask.Status == GenerationTaskStatus.Completed)
        {
            return new
            {
                task = existingTask,
                idempotent = true,
                targetPath = delivery.TargetPaths[0],
                message = "任务已经完成；保留既有项目产物。"
            };
        }

        EnsureClaimOwnershipForProcessing(existingTask, agentId);
        var task = await taskStore.RenewClaimAsync(existingTask.Id, agentId, cancellationToken)
            .ConfigureAwait(false);
        var submission = await _submissionService.SubmitAsync(
                task,
                delivery,
                agentId,
                outputIndex,
                sourcePath,
                cancellationToken)
            .ConfigureAwait(false);
        GenerationTask completed;
        using (var measurement = GenerationRunJournal.Measure(
                   delivery,
                   task,
                   "task.complete",
                   agentId,
                   outputIndex,
                   delivery.TargetPaths[0],
                   submission.TargetPath))
        {
            try
            {
                completed = await taskStore.CompleteAsync(
                        existingTask.Id,
                        agentId,
                        delivery.TargetPaths,
                        cancellationToken)
                    .ConfigureAwait(false);
                measurement.Complete(
                    "submit_generation_output 已自动完成单图任务登记。",
                    submission.TargetPath);
            }
            catch (Exception exception)
            {
                measurement.Fail(exception);
                throw;
            }
        }
        return new
        {
            task = completed,
            effectiveAssetRoot = delivery.AssetRoot,
            workspace = delivery.Workspace,
            submission,
            relativeAssetPaths = completed.RelativeAssetPaths,
            idempotent = false
        };
    }

    private static string BuildImageGenerationPrompt(GenerationTask task)
    {
        var constraints = task.Constraints;
        var output = constraints.Output;
        var divisor = GreatestCommonDivisor(output.Width, output.Height);
        var styleNode = constraints.Parameters?["style"] as JsonObject;
        var builder = new StringBuilder()
            .AppendLine("Create one production game-art raster asset for GAssetForge.")
            .Append("Final target canvas: ")
            .Append(output.Width)
            .Append('x')
            .Append(output.Height)
            .Append(" pixels; aspect ratio ")
            .Append(output.Width / divisor)
            .Append(':')
            .Append(output.Height / divisor)
            .AppendLine(". Preserve this composition and orientation exactly.")
            .AppendLine()
            .AppendLine("Content:")
            .AppendLine(constraints.Prompt.Trim());

        var styleDescription = ReadOptionalString(styleNode, "description");
        var stylePrompt = ReadOptionalString(styleNode, "prompt");
        if (!string.IsNullOrWhiteSpace(styleDescription) || !string.IsNullOrWhiteSpace(stylePrompt))
        {
            builder.AppendLine().AppendLine("Style:");
            if (!string.IsNullOrWhiteSpace(styleDescription))
            {
                builder.AppendLine(styleDescription.Trim());
            }

            if (!string.IsNullOrWhiteSpace(stylePrompt))
            {
                builder.AppendLine(stylePrompt.Trim());
            }
        }

        var requiredRules = constraints.Rules.Where(rule => rule.Required).ToArray();
        if (requiredRules.Length > 0)
        {
            builder.AppendLine().AppendLine("Required constraints (do not reinterpret or rotate):");
            foreach (var rule in requiredRules)
            {
                builder.Append("- [")
                    .Append(rule.Category)
                    .Append('/')
                    .Append(rule.Key)
                    .Append("] ")
                    .AppendLine(rule.Instruction.Trim());
            }
        }

        if (output.BackgroundRemoval is { } removal)
        {
            builder.AppendLine()
                .Append("Generation background: uniform opaque ")
                .Append(removal.ColorHex)
                .AppendLine(" across the full canvas and all pixels outside the asset silhouette. No gradient, texture, shadow, fog, checkerboard, or transparency in this first-stage model image.");
        }

        if (!string.IsNullOrWhiteSpace(constraints.NegativePrompt))
        {
            builder.AppendLine()
                .AppendLine("Negative constraints:")
                .AppendLine(constraints.NegativePrompt.Trim());
        }

        builder.AppendLine()
            .AppendLine("Return exactly one PNG image. Do not add labels, captions, borders, contact sheets, or alternate variants.");
        return builder.ToString();
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private async Task<object?> FailTaskAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var taskId = JsonArguments.RequiredString(arguments, "taskId");
        var agentId = JsonArguments.RequiredString(arguments, "agentId");
        var failureMessage = JsonArguments.RequiredString(arguments, "failureMessage");
        var existingTask = await taskStore.GetAsync(taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"生成任务不存在：{taskId}");
        var delivery = ReadDeliveryOrThrow(existingTask.Constraints);
        var task = await taskStore.FailAsync(taskId, agentId, failureMessage, cancellationToken)
            .ConfigureAwait(false);
        GenerationRunJournal.Append(
            delivery,
            new GenerationRunStageEvent
            {
                BatchId = task.BatchId,
                TaskId = task.Id,
                Stage = "task.fail",
                Status = "failed",
                EndedAtUtc = task.FailedAtUtc,
                AgentId = agentId,
                ErrorCode = "agent_reported_failure",
                Message = failureMessage
            });
        return new { task };
    }

    private static void EnsureOutputFormatsMatch(IEnumerable<string> relativePaths, string requestedFormat)
    {
        var normalizedFormat = requestedFormat.Trim().TrimStart('.').ToLowerInvariant();
        var acceptedExtensions = normalizedFormat switch
        {
            "jpg" or "jpeg" => new HashSet<string>([".jpg", ".jpeg"], StringComparer.OrdinalIgnoreCase),
            "tif" or "tiff" => new HashSet<string>([".tif", ".tiff"], StringComparer.OrdinalIgnoreCase),
            _ => new HashSet<string>([$".{normalizedFormat}"], StringComparer.OrdinalIgnoreCase)
        };

        foreach (var relativePath in relativePaths)
        {
            var extension = Path.GetExtension(relativePath);
            if (!acceptedExtensions.Contains(extension))
            {
                throw new InvalidDataException(
                    $"产物格式不匹配：任务要求 {requestedFormat}，文件 {relativePath} 的扩展名为 {extension}。");
            }
        }
    }

    private static GenerationDeliveryPlan? ReadDeliveryOrThrow(GenerationConstraintPackage package)
    {
        if (MaterialGenerationConstraintComposer.TryReadDelivery(package, out var delivery))
        {
            return delivery;
        }

        if (package.Parameters.ContainsKey("delivery"))
        {
            throw new InvalidDataException(
                "生成任务包含损坏或不受支持的 parameters.delivery，已拒绝按旧任务规则处理。");
        }

        return null;
    }

    private static bool PathsMatchExactly(
        IReadOnlyList<string> actualPaths,
        IReadOnlyList<string> expectedPaths)
    {
        if (actualPaths.Count != expectedPaths.Count)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        for (var index = 0; index < actualPaths.Count; index++)
        {
            if (!string.Equals(actualPaths[index], expectedPaths[index], comparison))
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureClaimOwnershipForProcessing(GenerationTask task, string agentId)
    {
        if (task.Status != GenerationTaskStatus.Claimed)
        {
            throw new InvalidOperationException(
                $"任务 {task.Id} 当前状态为 {task.Status}，只能处理处于 Claimed 的任务。");
        }

        if (!string.Equals(task.ClaimedBy, agentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"任务 {task.Id} 已由 Agent {task.ClaimedBy} 认领。");
        }
    }

    private static CurrentConstraintResult? BuildCurrentGenerationConstraints(
        WorkspaceContext context,
        string contentPrompt,
        string? stylePrompt,
        string? negativePrompt)
    {
        if (!string.Equals(context.ActiveWorkflow.Key, TilemapConstraintPackageBuilder.WorkflowId, StringComparison.Ordinal))
        {
            return null;
        }

        var input = new TilemapGenerationConstraintInput
        {
            GridUnitSize = context.Tilemap.GridUnitSize,
            ArtWidth = context.Tilemap.ArtImageWidth,
            ArtHeight = context.Tilemap.ArtImageHeight,
            DiamondAngle = context.Tilemap.DiamondAngle,
            RotationAngle = context.Tilemap.RotationAngle,
            ContentPrompt = contentPrompt,
            StylePrompt = stylePrompt,
            NegativePrompt = negativePrompt
        };
        return new CurrentConstraintResult(
            TilemapConstraintPackageBuilder.Build(input),
            TilemapConstraintPackageBuilder.CalculateMetrics(input));
    }

    private sealed record CurrentConstraintResult(
        GenerationConstraintPackage Constraints,
        TilemapGenerationMetrics Metrics);

    private static int ReadOptionalInt32(
        JsonElement arguments,
        string name,
        int defaultValue,
        int minimum,
        int maximum)
    {
        if (!arguments.TryGetProperty(name, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) ||
            number < minimum || number > maximum)
        {
            throw new JsonRpcException(
                JsonRpcErrorCodes.InvalidParams,
                $"参数 {name} 必须是 {minimum} 到 {maximum} 之间的整数。");
        }

        return number;
    }

    private static int ReadRequiredInt32(
        JsonElement arguments,
        string name,
        int minimum,
        int maximum)
    {
        if (!arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number) ||
            number < minimum ||
            number > maximum)
        {
            throw new JsonRpcException(
                JsonRpcErrorCodes.InvalidParams,
                $"参数 {name} 必须是 {minimum} 到 {maximum} 之间的整数。");
        }

        return number;
    }
}
