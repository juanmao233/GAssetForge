using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Generation;
using GAssetForge.McpServer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GAssetForge.SmokeTests;

/// <summary>
/// Deterministic regression loop for the production Art Workspace region-edit path. These tests
/// intentionally publish through ProductionArtWorkspaceGenerationGateway and submit through the
/// MCP catalog so a semantic-only region hint cannot satisfy the contract.
/// </summary>
internal static class ArtWorkspaceRegionEditRegressionSmokeTests
{
    private const int ImageSize = 16;
    private const int RegionX = 4;
    private const int RegionY = 4;
    private const int RegionWidth = 4;
    private const int RegionHeight = 4;
    private const string AgentId = "codex-art-workspace-region-edit-smoke";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private enum GatewayFailureMode
    {
        RegionValidation,
        TransientDelivery,
        PermanentDelivery
    }

    internal static async Task RunAllAsync(string temporaryRoot)
    {
        var failures = new List<string>();
        await CaptureFailureAsync(
            failures,
            "published region-edit contract",
            () => PublishedTaskFreezesAuthoritativeBaseAndMaskAsync(temporaryRoot));
        await CaptureFailureAsync(
            failures,
            "CLI fallback edit inheritance",
            () => CliFallbackInheritsBaseMaskAndRegionPromptAsync(temporaryRoot));
        await CaptureFailureAsync(
            failures,
            "corrupt region-edit claim rejection",
            () => CorruptDeclaredRegionEditFailsClosedBeforeClaimAsync(temporaryRoot));
        CaptureAssertion(
            failures,
            "region-edit Agent prompt routing",
            AgentPromptRoutesRegionEditDirectlyToFallback);
        CaptureAssertion(
            failures,
            "multi-image composition routing",
            MultiImageCompositionDoesNotBecomeRegionEdit);
        CaptureAssertion(
            failures,
            "arbitrary base size CLI mapping",
            ArbitraryBaseDimensionsUseNearAspectFlexibleCliSize);
        await CaptureFailureAsync(
            failures,
            "outside-mask deterministic composition",
            () => SubmissionRestoresEveryPixelOutsideMaskAsync(temporaryRoot));
        await CaptureFailureAsync(
            failures,
            "unprepared model size rejection",
            () => SubmissionRejectsUnpreparedMismatchedModelSizeAsync(temporaryRoot));
        await CaptureFailureAsync(
            failures,
            "outside-mask validation gate",
            () => SubmissionRejectsLargeOutsideMaskRedrawAsync(temporaryRoot));
        await CaptureFailureAsync(
            failures,
            "automatic retry artifact isolation",
            () => AutomaticRetryRebindsRegionEditArtifactsAsync(temporaryRoot));
        await CaptureFailureAsync(
            failures,
            "gateway region validation retry completion loop",
            () => GatewayKeepsOriginalWaiterUntilRegionEditRetrySucceedsAsync(
                temporaryRoot,
                GatewayFailureMode.RegionValidation));
        await CaptureFailureAsync(
            failures,
            "gateway transient fallback retry completion loop",
            () => GatewayKeepsOriginalWaiterUntilRegionEditRetrySucceedsAsync(
                temporaryRoot,
                GatewayFailureMode.TransientDelivery));
        await CaptureFailureAsync(
            failures,
            "gateway permanent fallback failure stays terminal",
            () => GatewayKeepsOriginalWaiterUntilRegionEditRetrySucceedsAsync(
                temporaryRoot,
                GatewayFailureMode.PermanentDelivery));

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Art Workspace 局部编辑回归仍未闭环：\n- " + string.Join("\n- ", failures));
        }
    }

    private static async Task PublishedTaskFreezesAuthoritativeBaseAndMaskAsync(
        string temporaryRoot)
    {
        var fixture = await PublishRegionEditTaskAsync(
            temporaryRoot,
            "region-edit-contract");
        var workspace = fixture.Task.Constraints.Parameters["artWorkspace"] as JsonObject
            ?? throw new InvalidOperationException("局部编辑任务缺少 parameters.artWorkspace。");
        var edit = workspace["editContract"] as JsonObject ?? workspace;
        var contractFailures = new List<string>();
        CaptureAssertion(contractFailures, "operationMode", () =>
            Equal("region-edit", ReadRequiredString(edit, "operationMode"),
                "局部 token 必须把任务升级为 region-edit，而不是普通整图生成"));
        CaptureAssertion(contractFailures, "authoritative base identity", () =>
            Equal(fixture.CurrentImageId, ReadRequiredString(edit, "baseImageId"),
                "instruction token 指向的当前图片必须是唯一 authoritative base"));
        CaptureAssertion(contractFailures, "authoritative base hash", () =>
            Equal(fixture.CurrentImageSha256, ReadRequiredString(edit, "baseImageSha256"),
                "authoritative base 必须冻结当前版本 SHA-256"));
        CaptureAssertion(contractFailures, "history policy", () =>
            Equal("exclude", ReadRequiredString(edit, "historyReferencePolicy"),
                "历史完整图片默认不得作为局部编辑输入"));
        CaptureAssertion(contractFailures, "preserveOutsideMask", () =>
            True(ReadRequiredBoolean(edit, "preserveOutsideMask"),
                "局部编辑必须要求软件强制保留 mask 外像素"));
        CaptureAssertion(contractFailures, "retry budget", () =>
            True(fixture.Task.RetryLineage?.MaxAttempts >= 3,
                "局部编辑任务必须冻结至少 3 次总尝试预算"));
        CaptureAssertion(contractFailures, "generated mask", () =>
        {
            var baseImagePath = ResolveFrozenPath(
                ReadRequiredString(edit, "baseImagePath"),
                fixture.AssetRoot,
                fixture.Paths.RootDirectory);
            var maskPath = ReadRequiredString(edit, "maskPath");
            var resolvedMaskPath = ResolveFrozenPath(
                maskPath,
                fixture.AssetRoot,
                fixture.Paths.RootDirectory);
            var previewPath = ResolveFrozenPath(
                ReadRequiredString(edit, "regionPreviewPath"),
                fixture.AssetRoot,
                fixture.Paths.RootDirectory);
            True(File.Exists(baseImagePath),
                $"发布任务时必须落盘 authoritative base：{baseImagePath}");
            True(File.Exists(resolvedMaskPath),
                $"发布任务时必须由 GAssetForge 生成真实 Alpha mask：{resolvedMaskPath}");
            Equal(ComputeSha256(resolvedMaskPath), ReadRequiredString(edit, "maskSha256"),
                "任务合同必须冻结实际 mask SHA-256");
            True(File.Exists(previewPath),
                $"发布任务时必须落盘局部区域预览：{previewPath}");
            AssertMask(resolvedMaskPath);
            var contractPath = Path.Combine(
                Path.GetDirectoryName(resolvedMaskPath)!,
                "contract.v1.json");
            True(File.Exists(contractPath),
                $"发布任务时必须落盘独立的局部编辑审计合同：{contractPath}");
        });
        CaptureAssertion(contractFailures, "expanded pixel bounds", () =>
        {
            var prompt = fixture.Task.Constraints.Prompt;
            True(prompt.Contains("x=4", StringComparison.Ordinal) &&
                 prompt.Contains("y=4", StringComparison.Ordinal) &&
                 prompt.Contains("width=4", StringComparison.Ordinal) &&
                 prompt.Contains("height=4", StringComparison.Ordinal),
                "最终提示词必须展开 sourcePixelBounds，不能只引用 parameters 内部对象名");
            True(prompt.Contains("outside", StringComparison.OrdinalIgnoreCase) ||
                 prompt.Contains("选区外", StringComparison.Ordinal),
                "最终提示词必须明确禁止修改 mask 外内容");
        });
        if (contractFailures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" | ", contractFailures));
        }

        var catalog = CreateCatalog(fixture);
        var claim = await catalog.CallAsync(
            "claim_generation_task",
            JsonSerializer.SerializeToElement(new { taskId = fixture.Task.Id, agentId = AgentId }),
            CancellationToken.None);
        var claimJson = JsonSerializer.SerializeToElement(claim, ProtocolJson.Options);
        False(claimJson.GetProperty("isError").GetBoolean(),
            "有效 region-edit 合同必须可以认领");
        var imageGeneration = claimJson.GetProperty("structuredContent").GetProperty("imageGeneration");
        var regionEdit = imageGeneration.GetProperty("regionEdit");
        Equal("region-edit", regionEdit.GetProperty("operationMode").GetString(),
            "claim imageGeneration 必须结构化返回 region-edit 模式");
        Equal(fixture.CurrentImageSha256, ComputeSha256(regionEdit.GetProperty("baseImagePath").GetString()!),
            "claim regionEdit 必须返回 authoritative base，而不是历史参考");
        True(File.Exists(regionEdit.GetProperty("maskPath").GetString()),
            "claim regionEdit 必须返回可访问的冻结 mask");
        True(regionEdit.GetProperty("preserveOutsideMask").GetBoolean(),
            "claim regionEdit 必须明确保留 mask 外像素");
        var claimReferences = imageGeneration.GetProperty("style").GetProperty("referenceSourcePaths");
        Equal(1, claimReferences.GetArrayLength(),
            "claim region-edit 只能交付唯一 authoritative base reference");
    }

    private static async Task CliFallbackInheritsBaseMaskAndRegionPromptAsync(string temporaryRoot)
    {
        var fixture = await PublishRegionEditTaskAsync(
            temporaryRoot,
            "region-edit-cli-fallback");
        var recorder = new RecordingFallbackService();
        var catalog = CreateCatalog(fixture, recorder);
        await ClaimAsync(catalog, fixture.Task.Id);

        _ = await catalog.CallAsync(
            "run_image_delivery_fallback",
            JsonSerializer.SerializeToElement(new { taskId = fixture.Task.Id, agentId = AgentId }),
            CancellationToken.None);
        var request = recorder.Request
            ?? throw new InvalidOperationException("run_image_delivery_fallback 未把冻结编辑合同传给 CLI service。");
        var fallbackFailures = new List<string>();
        CaptureAssertion(fallbackFailures, "unique --image", () =>
        {
            Equal(1, request.ReferenceSourcePaths.Count,
                "region-edit fallback 只能把 authoritative base 作为 --image，不能传入历史版本");
            Equal(fixture.CurrentImageSha256, ComputeSha256(request.ReferenceSourcePaths[0]),
                "region-edit fallback 第一张且唯一 --image 必须是 token 指向的当前版本");
        });
        CaptureAssertion(fallbackFailures, "operation mode", () =>
            Equal("region-edit", ReadRequiredProperty<string>(request, "OperationMode"),
                "fallback request 必须显式继承 region-edit 模式"));
        CaptureAssertion(fallbackFailures, "base image", () =>
        {
            var baseImagePath = ReadRequiredProperty<string>(request, "BaseImagePath");
            Equal(fixture.CurrentImageSha256, ComputeSha256(baseImagePath),
                "fallback request 必须显式携带 authoritative base 路径");
        });
        CaptureAssertion(fallbackFailures, "mask", () =>
        {
            var maskPath = ReadRequiredProperty<string>(request, "MaskPath");
            True(File.Exists(maskPath), "fallback request 必须携带已生成的真实 mask 路径");
        });
        CaptureAssertion(fallbackFailures, "preservation policy", () =>
            True(ReadRequiredProperty<bool>(request, "PreserveOutsideMask"),
                "fallback request 不得从 edit 降级为整图生成"));
        CaptureAssertion(fallbackFailures, "expanded prompt", () =>
            True(request.PromptText.Contains("x=4", StringComparison.Ordinal) &&
                 request.PromptText.Contains("width=4", StringComparison.Ordinal),
                "CLI fallback prompt 必须继承展开后的像素区域，而不是丢失局部编辑上下文"));
        if (fallbackFailures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" | ", fallbackFailures));
        }
    }

    private static async Task CorruptDeclaredRegionEditFailsClosedBeforeClaimAsync(string temporaryRoot)
    {
        var fixture = await PublishRegionEditTaskAsync(
            temporaryRoot,
            "region-edit-corrupt-claim-source");
        var corrupt = JsonSerializer.Deserialize<GenerationConstraintPackage>(
                          JsonSerializer.Serialize(fixture.Task.Constraints, GenerationJson.Options),
                          GenerationJson.Options)
                      ?? throw new InvalidOperationException("无法克隆待损坏的区域编辑约束。");
        var workspace = corrupt.Parameters["artWorkspace"] as JsonObject
            ?? throw new InvalidOperationException("损坏合同测试缺少 artWorkspace。");
        var edit = workspace["editContract"] as JsonObject
            ?? throw new InvalidOperationException("损坏合同测试缺少 editContract。");
        edit.Remove("maskPath");
        var corruptTask = await fixture.Store.CreateAsync(new CreateGenerationTaskRequest
        {
            DisplayName = "corrupt region edit",
            RequestedBy = "smoke-tests",
            Constraints = corrupt
        });
        var catalog = CreateCatalog(fixture);
        var claim = await catalog.CallAsync(
            "claim_generation_task",
            JsonSerializer.SerializeToElement(new { taskId = corruptTask.Id, agentId = AgentId }),
            CancellationToken.None);
        True(IsToolError(claim),
            "声明存在但无法读取的 region-edit 合同必须 fail-closed，不能降级为整图生成");
        var persisted = await fixture.Store.GetAsync(corruptTask.Id)
            ?? throw new InvalidOperationException("损坏合同任务丢失。");
        Equal(GenerationTaskStatus.Pending, persisted.Status,
            "损坏 region-edit 合同必须在 claim 前拒绝并保持 Pending");

        var aliased = JsonSerializer.Deserialize<GenerationConstraintPackage>(
                          JsonSerializer.Serialize(fixture.Task.Constraints, GenerationJson.Options),
                          GenerationJson.Options)
                      ?? throw new InvalidOperationException("无法克隆路径别名局部编辑约束。");
        var aliasedWorkspace = aliased.Parameters["artWorkspace"] as JsonObject
            ?? throw new InvalidOperationException("路径别名测试缺少 artWorkspace。");
        var aliasedEdit = aliasedWorkspace["editContract"] as JsonObject
            ?? throw new InvalidOperationException("路径别名测试缺少 editContract。");
        aliasedEdit["compositePath"] = aliasedEdit["baseImagePath"]?.GetValue<string>();
        var aliasedTask = await fixture.Store.CreateAsync(new CreateGenerationTaskRequest
        {
            DisplayName = "aliased region edit artifacts",
            RequestedBy = "smoke-tests",
            Constraints = aliased
        });
        var aliasedClaim = await catalog.CallAsync(
            "claim_generation_task",
            JsonSerializer.SerializeToElement(new { taskId = aliasedTask.Id, agentId = AgentId }),
            CancellationToken.None);
        True(IsToolError(aliasedClaim),
            "局部编辑可写工件路径与 authoritative base 重叠时必须 fail-closed，防止覆盖基准图");
    }

    private static void AgentPromptRoutesRegionEditDirectlyToFallback()
    {
        var prompt = GenerationAgentPromptBuilder.Build(["region-edit-prompt-fixture"]);
        True(prompt.Contains("imageGeneration.regionEdit", StringComparison.Ordinal) &&
             prompt.Contains("必须立即调用 run_image_delivery_fallback", StringComparison.Ordinal),
            "Agent prompt 必须在 claim 后先分流 region-edit 到 Harness fallback");
        True(prompt.Contains("region-edit 禁止先调用普通整图 image_gen/ImageGen", StringComparison.Ordinal),
            "Agent prompt 必须明确禁止 region-edit 先走普通整图 ImageGen");
        True(prompt.Contains("绝不能降级为普通生成", StringComparison.Ordinal),
            "损坏 region-edit 声明必须在 Agent 指令中 fail-closed");
    }

    private static void ArbitraryBaseDimensionsUseNearAspectFlexibleCliSize()
    {
        var resolved = GenerationToolProvider.ResolveCliSize(955, 1014);
        var parts = resolved.Size.Split('x');
        Equal(2, parts.Length, "CLI size 必须使用 WIDTHxHEIGHT 格式");
        var parsedWidth = int.TryParse(parts[0], out var width);
        var parsedHeight = int.TryParse(parts[1], out var height);
        True(parsedWidth && parsedHeight,
            "CLI size 必须包含有效整数尺寸");
        True(width % 16 == 0 && height % 16 == 0,
            "gpt-image-2 灵活尺寸必须按 16px 对齐");
        True((long)width * height is >= 655_360L and <= 8_294_400L,
            "gpt-image-2 灵活尺寸必须位于合法像素预算内");
        var expectedAspect = 955d / 1014d;
        var actualAspect = width / (double)height;
        var deviation = Math.Abs(actualAspect / expectedAspect - 1d);
        True(deviation <= 0.02d,
            $"任意 authoritative base 的 CLI 尺寸比例偏差必须不超过 2%；actual={width}x{height}");
    }

    private static void MultiImageCompositionDoesNotBecomeRegionEdit()
    {
        var armor = CreateConstraintReference("护甲参考", zIndex: 0);
        var character = CreateConstraintReference("角色参考", zIndex: 1);
        var package = ArtWorkspaceGenerationConstraintPackageBuilder.Build(
            new ArtWorkspaceGenerationConstraintInput
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Instruction = "使用 A 图片局部护甲，并组合 B 图片的完整角色。",
                OutputFrameName = "多图片组合输出",
                OutputFrameWidth = ImageSize,
                OutputFrameHeight = ImageSize,
                OutputPixelWidth = ImageSize,
                OutputPixelHeight = ImageSize,
                VisibleReferences = [armor, character],
                InstructionSegments =
                [
                    new ArtWorkspaceGenerationInstructionSegment
                    {
                        Kind = ArtWorkspaceGenerationInstructionSegmentKinds.Text,
                        Text = "使用这张"
                    },
                    new ArtWorkspaceGenerationInstructionSegment
                    {
                        Kind = ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
                        TokenId = Guid.NewGuid().ToString("N"),
                        ImageId = armor.ImageId,
                        Region = new ArtWorkspaceGenerationRegion
                        {
                            X = 0.25d,
                            Y = 0.25d,
                            Width = 0.25d,
                            Height = 0.25d
                        }
                    },
                    new ArtWorkspaceGenerationInstructionSegment
                    {
                        Kind = ArtWorkspaceGenerationInstructionSegmentKinds.Text,
                        Text = "图片的局部护甲，组合这张"
                    },
                    new ArtWorkspaceGenerationInstructionSegment
                    {
                        Kind = ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
                        TokenId = Guid.NewGuid().ToString("N"),
                        ImageId = character.ImageId
                    },
                    new ArtWorkspaceGenerationInstructionSegment
                    {
                        Kind = ArtWorkspaceGenerationInstructionSegmentKinds.Text,
                        Text = "图片的完整角色。"
                    }
                ]
            });

        False(ArtWorkspaceRegionEditContractComposer.TryResolveRequestedTarget(package, out var target),
            "包含两个图片 token 的组合任务不能因为其中一个 token 有局部区域就误进 region-edit");
        True(target is null, "多图片组合路由不得残留 authoritative edit target");
        False(ArtWorkspaceRegionEditContractComposer.HasDeclaredContract(package),
            "普通多图片组合合同不得声明 editContract");
        var workspace = package.Parameters["artWorkspace"] as JsonObject
            ?? throw new InvalidOperationException("多图片组合约束缺少 artWorkspace。");
        var references = workspace["instructionReferences"] as JsonArray
            ?? throw new InvalidOperationException("多图片组合约束缺少 instructionReferences。");
        Equal(2, references.Count,
            "多图片组合应保留两个有序引用，由普通生成链理解各自职责");
    }

    private static async Task SubmissionRestoresEveryPixelOutsideMaskAsync(string temporaryRoot)
    {
        var fixture = await PublishRegionEditTaskAsync(
            temporaryRoot,
            "region-edit-compose");
        var catalog = CreateCatalog(fixture);
        await ClaimAsync(catalog, fixture.Task.Id);

        var candidatePath = Path.Combine(fixture.AssetRoot, "ModelReturns", "small-outside-drift.png");
        WriteCandidate(fixture.CurrentImagePath, candidatePath, changeEveryOutsidePixel: false);
        var result = await SubmitAsync(catalog, fixture.Task.Id, candidatePath);
        False(IsToolError(result),
            "少量选区外模型漂移应由软件 mask 合成消除后完成，不应直接交付重绘像素");

        var persisted = await fixture.Store.GetAsync(fixture.Task.Id)
            ?? throw new InvalidOperationException("局部编辑提交后任务丢失。");
        Equal(GenerationTaskStatus.Completed, persisted.Status,
            "软件成功强制合成后任务应完成");
        var finalPath = ResolveAssetPath(fixture.AssetRoot, ReadSingleTargetPath(persisted));
        var outsideDifferences = CountOutsideMaskDifferences(
            fixture.CurrentImagePath,
            finalPath);
        Equal(0, outsideDifferences,
            "最终软件合成必须逐像素保留 authoritative base 的全部 mask 外内容");
    }

    private static async Task SubmissionRejectsUnpreparedMismatchedModelSizeAsync(
        string temporaryRoot)
    {
        var fixture = await PublishRegionEditTaskAsync(
            temporaryRoot,
            "region-edit-coordinate-remap");
        var catalog = CreateCatalog(fixture);
        await ClaimAsync(catalog, fixture.Task.Id);

        var candidatePath = Path.Combine(fixture.AssetRoot, "ModelReturns", "wide-api-edit.png");
        WriteWideScaledCandidate(fixture.CurrentImagePath, candidatePath);
        var result = await SubmitAsync(catalog, fixture.Task.Id, candidatePath);
        True(IsToolError(result),
            "submit 只接受 Harness 已无插值恢复为 base 尺寸的候选，不能自行 Stretch 或 contain-fit");

        var persisted = await fixture.Store.GetAsync(fixture.Task.Id)
            ?? throw new InvalidOperationException("尺寸映射提交后任务丢失。");
        False(persisted.Status == GenerationTaskStatus.Completed,
            "未经过 prepared canvas 逆变换的错尺寸候选不得登记 Completed");
    }

    private static async Task SubmissionRejectsLargeOutsideMaskRedrawAsync(string temporaryRoot)
    {
        var fixture = await PublishRegionEditTaskAsync(
            temporaryRoot,
            "region-edit-validation");
        var catalog = CreateCatalog(fixture);
        await ClaimAsync(catalog, fixture.Task.Id);

        var candidatePath = Path.Combine(fixture.AssetRoot, "ModelReturns", "whole-image-redraw.png");
        WriteCandidate(fixture.CurrentImagePath, candidatePath, changeEveryOutsidePixel: true);
        var result = await SubmitAsync(catalog, fixture.Task.Id, candidatePath);
        var persisted = await fixture.Store.GetAsync(fixture.Task.Id)
            ?? throw new InvalidOperationException("大面积选区外变化提交后任务丢失。");

        True(IsToolError(result) || persisted.Status != GenerationTaskStatus.Completed,
            "模型在 mask 外大面积重绘时必须被结构化验证拦截，不得登记 Completed");
        Equal(GenerationTaskStatus.Failed, persisted.Status,
            "超阈值选区外差异必须进入可重试 Failed，而不是成功或悬挂状态");
        Equal("output_region_edit_validation_failed", persisted.FailureCode,
            "大面积重绘必须使用局部编辑验证外层错误码");
        Equal("art_workspace_region_edit_outside_mask_changed", persisted.FailureSubCode,
            "大面积重绘必须保留选区外差异子错误码");
        Equal("output.region_edit_validation", persisted.FailureStage,
            "大面积重绘必须记录准确失败阶段");
        Equal(true, persisted.FailureRetryable,
            "大面积重绘必须允许按冻结重试预算继续尝试");
        var resultJson = JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
        False(resultJson.GetProperty("isError").GetBoolean(),
            "约束验证失败是已处理的业务结果，不应伪装成 MCP/Runtime 工具故障");
        var structured = resultJson.GetProperty("structuredContent");
        Equal("retryable_validation_failure", structured.GetProperty("outcome").GetString(),
            "大面积重绘必须结构化返回可重试验证失败");
        Equal("output_region_edit_validation_failed",
            structured.GetProperty("failure").GetProperty("code").GetString(),
            "MCP 结果必须向 Agent 返回准确局部编辑错误码");
        var edit = persisted.Constraints.Parameters["artWorkspace"]?["editContract"] as JsonObject
            ?? throw new InvalidOperationException("失败任务缺少局部编辑审计合同。");
        foreach (var property in new[]
                 {
                     "compositePath",
                     "differenceReportPath",
                     "differencePreviewPath"
                 })
        {
            var artifactPath = ResolveFrozenPath(
                ReadRequiredString(edit, property),
                fixture.AssetRoot,
                fixture.Paths.RootDirectory);
            True(File.Exists(artifactPath),
                $"局部编辑失败时仍必须保留 {property} 中间产物：{artifactPath}");
        }
    }

    private static async Task AutomaticRetryRebindsRegionEditArtifactsAsync(string temporaryRoot)
    {
        var fixture = await PublishRegionEditTaskAsync(
            temporaryRoot,
            "region-edit-automatic-retry",
            "Generated/ArtWorkspace/Characters");
        await fixture.Store.ClaimAsync(fixture.Task.Id, AgentId);
        var failed = await fixture.Store.FailAsync(
            fixture.Task.Id,
            AgentId,
            new GenerationTaskFailure
            {
                Message =
                    "output_region_edit_validation_failed: " +
                    "art_workspace_region_edit_outside_mask_changed; outside=48.18%",
                Code = "output_region_edit_validation_failed",
                SubCode = "art_workspace_region_edit_outside_mask_changed",
                Stage = "output.region_edit_validation",
                Retryable = true
            });
        True(ArtWorkspaceRegionEditContractComposer.TryRead(failed.Constraints, out var parentContract),
            "局部编辑父任务必须具有可读取合同");

        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(fixture.Paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(
            new ThemeService(),
            new UserSettingsService(fixture.Paths),
            editor);
        using var retryGeneration = new AssetGenerationViewModel(
            fixture.Paths,
            fixture.Store,
            shell.ArtStyleLibrary,
            shell.TilemapWorkspace,
            shell.ProjectBrowser,
            shell.AgentConnection,
            new DesktopAssetGenerationOutputPicker(),
            shell.BackgroundRemoval,
            autoRetryEnabled: true,
            autoRetryCount: 2);

        var result = await retryGeneration.TryCreateAutomaticRetryBatchAsync(failed.BatchId!);
        True(result.Scheduled,
            $"局部编辑验证失败应创建新的自动重做尝试：{result.Message}");
        var refreshedParent = await fixture.Store.GetAsync(failed.Id)
            ?? throw new InvalidOperationException("局部编辑重做父任务丢失。");
        var child = await fixture.Store.GetAsync(refreshedParent.SupersededByTaskId!)
            ?? throw new InvalidOperationException("局部编辑重做 child 丢失。");
        True(ArtWorkspaceRegionEditContractComposer.TryRead(child.Constraints, out var childContract),
            "局部编辑重做 child 必须保留可读取合同");
        Equal(failed.Id, child.RetryLineage?.PreviousTaskId,
            "通用恢复 retry child 必须指回准确父任务");
        Equal(failed.RetryLineage?.RootTaskId, child.RetryLineage?.RootTaskId,
            "通用恢复 retry child 必须保留谱系根");
        Equal(failed.RetryLineage?.AttemptNumber + 1, child.RetryLineage?.AttemptNumber,
            "通用恢复 retry child 必须推进一次尝试编号");
        Equal(failed.RetryLineage?.MaxAttempts, child.RetryLineage?.MaxAttempts,
            "通用恢复 retry child 不得改写冻结尝试上限");
        var childTargetPath = ReadSingleTargetPath(child);
        var targetSeparator = childTargetPath.LastIndexOf('/');
        True(targetSeparator > 0, "通用恢复 retry child 必须保留 Assets 相对目标目录");
        Equal(
            $"Generated/ArtWorkspace/Characters/{child.BatchId}/Final",
            childTargetPath[..targetSeparator],
            "art-workspace 通用恢复 retry 必须保留自定义输出前缀并替换父 batch 目录，" +
            "不能嵌套到父 Final/ArtWorkspace");
        True(
            childTargetPath[(targetSeparator + 1)..].StartsWith(
                $"art_workspace_{child.BatchId![..8]}_",
                StringComparison.Ordinal),
            "art-workspace 通用恢复 retry 必须使用稳定的 art_workspace 文件名前缀");

        foreach (var pair in new[]
                 {
                     (parentContract!.BaseImagePath, childContract!.BaseImagePath, "authoritative base"),
                     (parentContract.MaskPath, childContract.MaskPath, "mask"),
                     (parentContract.RegionPreviewPath, childContract.RegionPreviewPath, "region preview"),
                     (parentContract.CompositePath, childContract.CompositePath, "composite"),
                     (parentContract.DifferenceReportPath, childContract.DifferenceReportPath, "difference report"),
                     (parentContract.DifferencePreviewPath, childContract.DifferencePreviewPath, "difference preview")
                 })
        {
            False(string.Equals(pair.Item1, pair.Item2, StringComparison.OrdinalIgnoreCase),
                $"自动重做必须为 {pair.Item3} 分配独立路径，不能复用上一轮失败工件");
        }

        var childBasePath = ResolveAssetPath(fixture.AssetRoot, childContract!.BaseImagePath);
        var childMaskPath = ResolveAssetPath(fixture.AssetRoot, childContract.MaskPath);
        True(File.Exists(childBasePath), "自动重做发布前必须复制 authoritative base");
        True(File.Exists(childMaskPath), "自动重做发布前必须生成独立 mask");
        Equal(parentContract!.BaseImageSha256, ComputeSha256(childBasePath),
            "自动重做 authoritative base 必须保持原冻结哈希");
        Equal(parentContract.MaskSha256, ComputeSha256(childMaskPath),
            "自动重做 mask 必须保持原冻结哈希");
    }

    private static async Task GatewayKeepsOriginalWaiterUntilRegionEditRetrySucceedsAsync(
        string temporaryRoot,
        GatewayFailureMode failureMode)
    {
        var root = Path.Combine(
            temporaryRoot,
            $"region-edit-gateway-retry-loop-{failureMode}",
            Guid.NewGuid().ToString("N"));
        var paths = new WorkspacePaths(Path.Combine(root, "app-data"));
        var projectRoot = Path.Combine(root, "project");
        var assetRoot = Path.Combine(projectRoot, "Assets");
        var sourceDirectory = Path.Combine(assetRoot, "ArtWorkspace", "References");
        Directory.CreateDirectory(sourceDirectory);
        var currentPath = Path.Combine(sourceDirectory, "current.png");
        var oldBluePath = Path.Combine(sourceDirectory, "history-blue-a.png");
        var olderBluePath = Path.Combine(sourceDirectory, "history-blue-b.png");
        WriteCurrentImage(currentPath);
        WriteHistoryImage(oldBluePath, new Rgba32(25, 70, 210, 255));
        WriteHistoryImage(olderBluePath, new Rgba32(40, 90, 230, 255));
        var currentImageId = Guid.NewGuid().ToString("N");
        var currentSha256 = ComputeSha256(currentPath);
        var store = new JsonGenerationTaskStore(paths.GenerationDirectory);
        var server = new McpServerProcessService(paths);
        var runtime = new CodexAgentRuntimeService("codex-region-edit-retry-loop");
        using var agentConnection = new AgentConnectionViewModel(
            server,
            runtime,
            selectedAgentKey: "codex",
            workingDirectory: projectRoot);
        using var console = new WorkbenchConsoleService(paths);
        var dispatches = new List<(string BatchId, string TaskId)>();
        var initialDispatch = new TaskCompletionSource<(string BatchId, string TaskId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var gateway = new ProductionArtWorkspaceGenerationGateway(
            paths,
            store,
            agentConnection,
            (batchId, taskIds, isRetry) =>
            {
                var dispatch = (batchId, taskIds.Single());
                lock (dispatches)
                {
                    if (dispatches.Count == 0)
                    {
                        False(isRetry, "首轮 Art Workspace 发布不得标记为 retry dispatch");
                    }
                    else
                    {
                        True(isRetry, "region-edit child 必须以 isRetry=true 进入 Agent 调度器");
                    }

                    dispatches.Add(dispatch);
                }

                initialDispatch.TrySetResult(dispatch);
                return GenerationBatchDispatchEnqueueResult.Enqueued;
            },
            console,
            isConnected: static () => true,
            isTurnRunning: static () => false,
            agentDisplayName: static () => "Codex region edit retry fixture");
        var generation = gateway.GenerateAsync(CreateRequest(
            projectRoot,
            assetRoot,
            currentImageId,
            currentPath,
            currentSha256,
            oldBluePath,
            olderBluePath));
        var firstDispatch = await initialDispatch.Task.WaitAsync(Timeout);
        var firstTask = await store.GetAsync(firstDispatch.TaskId)
            ?? throw new InvalidOperationException("找不到局部编辑首轮任务。");
        var fixture = new PublishedFixture(
            paths,
            assetRoot,
            store,
            firstTask,
            currentImageId,
            currentPath,
            currentSha256);
        var catalog = CreateCatalog(fixture);
        await ClaimAsync(catalog, firstTask.Id);
        if (failureMode == GatewayFailureMode.RegionValidation)
        {
            var rejectedCandidate = Path.Combine(assetRoot, "ModelReturns", "attempt-01-redraw.png");
            WriteCandidate(currentPath, rejectedCandidate, changeEveryOutsidePixel: true);
            _ = await SubmitAsync(catalog, firstTask.Id, rejectedCandidate);
        }
        else
        {
            var transient = failureMode == GatewayFailureMode.TransientDelivery;
            _ = await store.FailAsync(
                firstTask.Id,
                AgentId,
                new GenerationTaskFailure
                {
                    Message = transient
                        ? "image_delivery_fallback_failed: HTTP 429 temporarily unavailable"
                        : "image_delivery_fallback_failed: HTTP 401 invalid API credentials",
                    Code = "image_delivery_fallback_failed",
                    Stage = "image.cli_fallback",
                    Retryable = transient
                });
        }

        var failedFirstTask = await store.GetAsync(firstTask.Id)
            ?? throw new InvalidOperationException("首轮局部编辑失败后任务丢失。");
        Equal(GenerationTaskStatus.Failed, failedFirstTask.Status,
            "首轮局部编辑错误必须先持久化为 Failed");

        await gateway.ReportDispatchCompletedAsync(
            CreateDispatchCompletion(firstDispatch.BatchId, firstTask.Id, isRetry: false),
            new GenerationBatchTurnReconciliationResult(
                firstDispatch.BatchId,
                TotalCount: 1,
                PendingCount: 0,
                ClaimedCount: 0,
                CompletedCount: 0,
                FailedCount: 1,
                AutoFailedCount: 0,
                FailureDetail: failedFirstTask.FailureMessage),
            preservePendingTasks: false);

        if (failureMode == GatewayFailureMode.PermanentDelivery)
        {
            var terminal = await generation.WaitAsync(Timeout);
            False(terminal.IsSuccess,
                "不可重试的 region-edit fallback 错误必须终止原 waiter，不能创建 child");
            lock (dispatches)
            {
                Equal(1, dispatches.Count,
                    "不可重试的 region-edit fallback 错误不得排队 retry child");
            }

            var terminalParent = await store.GetAsync(firstTask.Id)
                ?? throw new InvalidOperationException("不可重试 fallback 失败后父任务丢失。");
            True(string.IsNullOrWhiteSpace(terminalParent.SupersededByTaskId),
                "不可重试 fallback 失败不得写入 SupersededByTaskId");
            False(gateway.OwnsBatch(firstDispatch.BatchId),
                "不可重试 fallback 失败完成 waiter 后必须释放批次 owner");
            return;
        }

        False(generation.IsCompleted,
            "首轮可重试 region-edit 失败后原 Art Workspace waiter 必须继续等待 child，而不是立即失败");
        (string BatchId, string TaskId) retryDispatch;
        lock (dispatches)
        {
            Equal(2, dispatches.Count,
                "首轮可重试校验失败后 Gateway 必须立即排队唯一 retry child");
            retryDispatch = dispatches[1];
        }

        var refreshedParent = await store.GetAsync(firstTask.Id)
            ?? throw new InvalidOperationException("创建 retry child 后父任务丢失。");
        Equal(retryDispatch.TaskId, refreshedParent.SupersededByTaskId,
            "retry child 必须原子接替首轮失败任务");
        var retryTask = await store.GetAsync(retryDispatch.TaskId)
            ?? throw new InvalidOperationException("找不到 Gateway 排队的 region-edit retry child。");
        Equal(firstTask.Id, retryTask.RetryLineage?.PreviousTaskId,
            "Gateway retry child 必须指回准确父任务");
        Equal(firstTask.RetryLineage?.RootTaskId, retryTask.RetryLineage?.RootTaskId,
            "Gateway retry child 必须保留谱系根");
        Equal(2, retryTask.RetryLineage?.AttemptNumber,
            "Gateway 排队的 child 必须是第 2 次冻结尝试");
        Equal(firstTask.RetryLineage?.MaxAttempts, retryTask.RetryLineage?.MaxAttempts,
            "Gateway retry child 不得改写冻结尝试上限");
        True(retryTask.DispatchIntent is { Requested: true, IsRetry: true },
            "retry child 必须保留自动派发且 IsRetry=true 的持久意图");
        True(MaterialGenerationConstraintComposer.TryReadDelivery(retryTask.Constraints, out var retryDelivery) &&
             retryDelivery is not null,
            "retry child 必须保留有效 delivery");
        Equal(0, retryDelivery!.StagingPaths.Length,
            "无背景剔除的 region-edit retry 必须保持 StagingPaths=[]");
        Equal(
            $"Generated/ArtWorkspace/{retryTask.BatchId}/Final",
            retryDelivery.OutputDirectory,
            "Gateway retry child 必须使用独立 ArtWorkspace child Final 目录");
        if (failureMode == GatewayFailureMode.TransientDelivery)
        {
            True(
                retryTask.Constraints.Prompt.Contains("临时交付阶段失败", StringComparison.Ordinal),
                "临时 fallback retry 的定向提示必须描述交付失败，不能伪报为 mask 外重绘");
        }
        var hasParentContract = ArtWorkspaceRegionEditContractComposer.TryRead(
            failedFirstTask.Constraints,
            out var parentContract);
        var hasRetryContract = ArtWorkspaceRegionEditContractComposer.TryRead(
            retryTask.Constraints,
            out var retryContract);
        True(hasParentContract && parentContract is not null &&
             hasRetryContract && retryContract is not null,
            "父任务与 retry child 都必须保留可读取 region-edit 合同");
        Equal(parentContract!.BaseImageSha256, retryContract!.BaseImageSha256,
            "retry child 必须继承 authoritative base 哈希");
        Equal(parentContract.MaskSha256, retryContract.MaskSha256,
            "retry child 必须继承冻结 mask 哈希");
        Equal(parentContract.SourcePixelBounds, retryContract.SourcePixelBounds,
            "retry child 必须继承同一源像素选区");
        False(string.Equals(
                parentContract.BaseImagePath,
                retryContract.BaseImagePath,
                StringComparison.OrdinalIgnoreCase),
            "retry child 必须使用新 Run 中的 authoritative base 工件");
        False(string.Equals(
                parentContract.CompositePath,
                retryContract.CompositePath,
                StringComparison.OrdinalIgnoreCase),
            "retry child 不得覆盖首轮失败的 safe composite");

        await ClaimAsync(catalog, retryTask.Id);
        var retryBasePath = ResolveAssetPath(assetRoot, retryContract.BaseImagePath);
        var acceptedCandidate = Path.Combine(assetRoot, "ModelReturns", "attempt-02-local-edit.png");
        WriteCandidate(retryBasePath, acceptedCandidate, changeEveryOutsidePixel: false);
        var retrySubmission = await SubmitAsync(catalog, retryTask.Id, acceptedCandidate);
        False(IsToolError(retrySubmission),
            "第 2 次仅局部变化的候选必须通过 region-edit 提交");
        var completedRetryTask = await store.GetAsync(retryTask.Id)
            ?? throw new InvalidOperationException("第 2 次提交后 retry child 丢失。");
        Equal(GenerationTaskStatus.Completed, completedRetryTask.Status,
            "第 2 次局部编辑成功后 child 必须登记 Completed");

        await gateway.ReportDispatchCompletedAsync(
            CreateDispatchCompletion(retryDispatch.BatchId, retryTask.Id, isRetry: true),
            new GenerationBatchTurnReconciliationResult(
                retryDispatch.BatchId,
                TotalCount: 1,
                PendingCount: 0,
                ClaimedCount: 0,
                CompletedCount: 1,
                FailedCount: 0,
                AutoFailedCount: 0,
                FailureDetail: null),
            preservePendingTasks: false);
        var result = await generation.WaitAsync(Timeout);
        True(result.IsSuccess,
            "retry child 成功后原 Art Workspace waiter 必须收到成功结果");
        Equal(retryTask.Id, result.TaskId,
            "原 waiter 的最终 taskId 必须指向成功的 retry child");
        var retryTargetPath = ResolveAssetPath(assetRoot, ReadSingleTargetPath(completedRetryTask));
        Equal(retryTargetPath, result.OutputPaths.Single(),
            "原 waiter 必须交付 retry child 的冻结最终目标");
        Equal(0, CountOutsideMaskDifferences(retryBasePath, retryTargetPath),
            "retry child Final 的 mask 外像素必须继续逐像素等于 authoritative base");
    }

    private static GenerationBatchDispatchCompletedEventArgs CreateDispatchCompletion(
        string batchId,
        string taskId,
        bool isRetry) =>
        new(
            new GenerationBatchDispatchRequest(batchId, [taskId], isRetry),
            new AgentTurnResult(
                "fixture-thread",
                $"fixture-turn-{taskId[..8]}",
                "completed",
                null),
            null);

    private static async Task<PublishedFixture> PublishRegionEditTaskAsync(
        string temporaryRoot,
        string testName,
        string outputRelativeDirectory = "Generated/ArtWorkspace")
    {
        var root = Path.Combine(temporaryRoot, testName, Guid.NewGuid().ToString("N"));
        var paths = new WorkspacePaths(Path.Combine(root, "app-data"));
        var projectRoot = Path.Combine(root, "project");
        var assetRoot = Path.Combine(projectRoot, "Assets");
        var sourceDirectory = Path.Combine(assetRoot, "ArtWorkspace", "References");
        Directory.CreateDirectory(sourceDirectory);
        var currentPath = Path.Combine(sourceDirectory, "current.png");
        var oldBluePath = Path.Combine(sourceDirectory, "history-blue-a.png");
        var olderBluePath = Path.Combine(sourceDirectory, "history-blue-b.png");
        WriteCurrentImage(currentPath);
        WriteHistoryImage(oldBluePath, new Rgba32(25, 70, 210, 255));
        WriteHistoryImage(olderBluePath, new Rgba32(40, 90, 230, 255));
        var currentImageId = Guid.NewGuid().ToString("N");
        var currentSha256 = ComputeSha256(currentPath);
        var store = new JsonGenerationTaskStore(paths.GenerationDirectory);
        var server = new McpServerProcessService(paths);
        var runtime = new CodexAgentRuntimeService("codex-region-edit-fixture");
        using var agentConnection = new AgentConnectionViewModel(
            server,
            runtime,
            selectedAgentKey: "codex",
            workingDirectory: projectRoot);
        using var console = new WorkbenchConsoleService(paths);
        var publishedSource = new TaskCompletionSource<(string BatchId, string TaskId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var gateway = new ProductionArtWorkspaceGenerationGateway(
            paths,
            store,
            agentConnection,
            (batchId, taskIds, _) =>
            {
                publishedSource.TrySetResult((batchId, taskIds.Single()));
                return GenerationBatchDispatchEnqueueResult.QueueStopped;
            },
            console,
            isConnected: static () => true,
            isTurnRunning: static () => false,
            agentDisplayName: static () => "Codex region edit fixture");
        using var cancellation = new CancellationTokenSource();
        var generation = gateway.GenerateAsync(
            CreateRequest(
                projectRoot,
                assetRoot,
                currentImageId,
                currentPath,
                currentSha256,
                oldBluePath,
                olderBluePath,
                outputRelativeDirectory),
            cancellationToken: cancellation.Token);
        var published = await publishedSource.Task.WaitAsync(Timeout);
        var task = await store.GetAsync(published.TaskId)
            ?? throw new InvalidOperationException("找不到已发布的局部编辑持久任务。");
        cancellation.Cancel();
        await ExpectCanceledAsync(generation);
        return new PublishedFixture(
            paths,
            assetRoot,
            store,
            task,
            currentImageId,
            currentPath,
            currentSha256);
    }

    private static ArtWorkspaceGenerationRequest CreateRequest(
        string projectRoot,
        string assetRoot,
        string currentImageId,
        string currentPath,
        string currentSha256,
        string oldBluePath,
        string olderBluePath,
        string outputRelativeDirectory = "Generated/ArtWorkspace")
    {
        var tokenId = Guid.NewGuid().ToString("N");
        return new ArtWorkspaceGenerationRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ProjectRoot = projectRoot,
            AssetRoot = assetRoot,
            Instruction = "将角色的这只眼睛修改为红色。",
            OutputFrame = new ArtWorkspaceGenerationOutputFrameSnapshot
            {
                Name = "局部编辑输出",
                Width = ImageSize,
                Height = ImageSize,
                PixelWidth = ImageSize,
                PixelHeight = ImageSize,
                BackgroundHex = "#20242C",
                OutputRelativeDirectory = outputRelativeDirectory
            },
            VisibleReferences =
            [
                CreateReference(Guid.NewGuid().ToString("N"), oldBluePath, 0, ComputeSha256(oldBluePath)),
                CreateReference(currentImageId, currentPath, 1, currentSha256),
                CreateReference(Guid.NewGuid().ToString("N"), olderBluePath, 2, ComputeSha256(olderBluePath))
            ],
            InstructionSegments =
            [
                new ArtWorkspaceGenerationInstructionSegment
                {
                    Kind = ArtWorkspaceGenerationInstructionSegmentKinds.Text,
                    Text = "将角色的这只"
                },
                new ArtWorkspaceGenerationInstructionSegment
                {
                    Kind = ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
                    TokenId = tokenId,
                    ImageId = currentImageId,
                    Region = new ArtWorkspaceGenerationRegion
                    {
                        X = 0.25d,
                        Y = 0.25d,
                        Width = 0.25d,
                        Height = 0.25d
                    }
                },
                new ArtWorkspaceGenerationInstructionSegment
                {
                    Kind = ArtWorkspaceGenerationInstructionSegmentKinds.Text,
                    Text = "眼睛修改为红色。"
                }
            ]
        };
    }

    private static ArtWorkspaceGenerationReferenceSnapshot CreateReference(
        string imageId,
        string path,
        int zIndex,
        string sha256) =>
        new()
        {
            ImageId = imageId,
            AssetId = Guid.NewGuid().ToString("N"),
            Name = "character.png",
            AbsolutePath = path,
            Sha256 = sha256,
            MediaType = "image/png",
            PixelWidth = ImageSize,
            PixelHeight = ImageSize,
            Width = ImageSize,
            Height = ImageSize,
            Opacity = 1d,
            ZIndex = zIndex
        };

    private static ArtWorkspaceGenerationReference CreateConstraintReference(
        string name,
        int zIndex) =>
        new()
        {
            ImageId = Guid.NewGuid().ToString("N"),
            AssetId = Guid.NewGuid().ToString("N"),
            Name = name,
            Sha256 = new string((char)('a' + zIndex), 64),
            MediaType = "image/png",
            DataRootRelativePath = $"Generation/ReferenceSnapshots/{Guid.NewGuid():N}/{name}.png",
            PixelWidth = ImageSize,
            PixelHeight = ImageSize,
            Width = ImageSize,
            Height = ImageSize,
            Opacity = 1d,
            ZIndex = zIndex
        };

    private static McpToolCatalog CreateCatalog(
        PublishedFixture fixture,
        IImageGenerationCliFallbackService? fallback = null)
    {
        var options = new ServerOptions(
            fixture.Paths.RootDirectory,
            McpTransport.Stdio,
            IPAddress.Loopback,
            43120,
            "codex",
            null,
            null);
        return new GenerationToolProvider(
                fixture.Store,
                new WorkspaceContextResolver(options),
                options,
                fixture.Store.StoreFilePath,
                fallback)
            .CreateCatalog();
    }

    private static async Task ClaimAsync(McpToolCatalog catalog, string taskId)
    {
        var claim = await catalog.CallAsync(
            "claim_generation_task",
            JsonSerializer.SerializeToElement(new { taskId, agentId = AgentId }),
            CancellationToken.None);
        False(IsToolError(claim), "局部编辑回归任务必须可由固定 Agent 认领");
    }

    private static Task<object> SubmitAsync(
        McpToolCatalog catalog,
        string taskId,
        string sourcePath) =>
        catalog.CallAsync(
            "submit_generation_output",
            JsonSerializer.SerializeToElement(new
            {
                taskId,
                agentId = AgentId,
                outputIndex = 0,
                sourcePath
            }),
            CancellationToken.None);

    private static void WriteCurrentImage(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(ImageSize, ImageSize);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < ImageSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < ImageSize; x++)
                {
                    row[x] = new Rgba32((byte)(120 + x), (byte)(20 + y), (byte)(30 + x + y), 255);
                }
            }
        });
        image.Save(path, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
    }

    private static void WriteHistoryImage(string path, Rgba32 color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(ImageSize, ImageSize, color);
        image.Save(path, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
    }

    private static void WriteCandidate(
        string basePath,
        string targetPath,
        bool changeEveryOutsidePixel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using var image = Image.Load<Rgba32>(basePath);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < ImageSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < ImageSize; x++)
                {
                    if (IsInsideRegion(x, y))
                    {
                        row[x] = new Rgba32(240, 25, 35, 255);
                    }
                    else if (changeEveryOutsidePixel)
                    {
                        row[x] = new Rgba32(15, 180, 210, 255);
                    }
                }
            }

            if (!changeEveryOutsidePixel)
            {
                accessor.GetRowSpan(0)[0] = new Rgba32(15, 180, 210, 255);
            }
        });
        image.Save(targetPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
    }

    private static void WriteWideScaledCandidate(string basePath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using var image = Image.Load<Rgba32>(basePath);
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new Size(48, 16),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));
        for (var y = 4; y < 8; y++)
        {
            for (var x = 12; x < 24; x++)
            {
                image[x, y] = new Rgba32(240, 25, 35, 255);
            }
        }

        image.Save(targetPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
    }

    private static int CountOutsideMaskDifferences(string basePath, string outputPath)
    {
        using var baseline = Image.Load<Rgba32>(basePath);
        using var output = Image.Load<Rgba32>(outputPath);
        Equal(ImageSize, output.Width, "局部编辑最终宽度");
        Equal(ImageSize, output.Height, "局部编辑最终高度");
        var differences = 0;
        for (var y = 0; y < ImageSize; y++)
        {
            for (var x = 0; x < ImageSize; x++)
            {
                if (!IsInsideRegion(x, y) && baseline[x, y] != output[x, y])
                {
                    differences++;
                }
            }
        }

        return differences;
    }

    private static void AssertMask(string path)
    {
        using var mask = Image.Load<Rgba32>(path);
        Equal(ImageSize, mask.Width, "region mask 宽度必须匹配 authoritative base");
        Equal(ImageSize, mask.Height, "region mask 高度必须匹配 authoritative base");
        var insideAlpha = mask[RegionX, RegionY].A;
        var outsideAlpha = mask[0, 0].A;
        True(insideAlpha is byte.MinValue or byte.MaxValue &&
             outsideAlpha is byte.MinValue or byte.MaxValue &&
             insideAlpha != outsideAlpha,
            "region mask 必须以互斥的 0/255 Alpha 明确区分可编辑区和保留区");
        for (var y = 0; y < ImageSize; y++)
        {
            for (var x = 0; x < ImageSize; x++)
            {
                var expected = IsInsideRegion(x, y) ? insideAlpha : outsideAlpha;
                Equal(expected, mask[x, y].A,
                    $"region mask Alpha 错误 ({x},{y})");
            }
        }
    }

    private static bool IsInsideRegion(int x, int y) =>
        x >= RegionX && x < RegionX + RegionWidth &&
        y >= RegionY && y < RegionY + RegionHeight;

    private static string ResolveFrozenPath(string path, params string[] roots)
    {
        if (Path.IsPathFullyQualified(path))
        {
            return Path.GetFullPath(path);
        }

        foreach (var root in roots)
        {
            var candidate = Path.GetFullPath(Path.Combine(
                root,
                path.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(Path.Combine(
            roots[0],
            path.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string ResolveAssetPath(string assetRoot, string relativePath) =>
        Path.GetFullPath(Path.Combine(
            assetRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string ReadSingleTargetPath(GenerationTask task)
    {
        var delivery = task.Constraints.Parameters["delivery"] as JsonObject
            ?? throw new InvalidOperationException("局部编辑任务缺少 parameters.delivery。");
        var targets = delivery["targetPaths"] as JsonArray
            ?? throw new InvalidOperationException("局部编辑任务缺少 delivery.targetPaths。");
        return targets.Single()?.GetValue<string>()
            ?? throw new InvalidOperationException("局部编辑任务没有唯一冻结目标。");
    }

    private static string ReadRequiredString(JsonObject node, string propertyName) =>
        node[propertyName]?.GetValue<string>()
        ?? throw new InvalidOperationException($"局部编辑合同缺少 {propertyName}。");

    private static bool ReadRequiredBoolean(JsonObject node, string propertyName) =>
        node[propertyName]?.GetValue<bool>()
        ?? throw new InvalidOperationException($"局部编辑合同缺少 {propertyName}。");

    private static T ReadRequiredProperty<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException(
                $"{instance.GetType().Name} 缺少 {propertyName}，fallback 会丢失局部编辑合同。");
        var value = property.GetValue(instance);
        return value is T typed
            ? typed
            : throw new InvalidOperationException(
                $"{instance.GetType().Name}.{propertyName} 类型或值无效。");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsToolError(object? value)
    {
        var json = JsonSerializer.SerializeToElement(value, ProtocolJson.Options);
        return json.TryGetProperty("isError", out var isError) && isError.GetBoolean();
    }

    private static async Task ExpectCanceledAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Timeout);
            throw new InvalidOperationException("预期已发布任务的 UI waiter 被取消。");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task CaptureFailureAsync(
        ICollection<string> failures,
        string label,
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            failures.Add($"{label}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void CaptureAssertion(
        ICollection<string> failures,
        string label,
        Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add($"{label}: {exception.Message}");
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message}。Expected={expected}; Actual={actual}");
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

    private sealed record PublishedFixture(
        WorkspacePaths Paths,
        string AssetRoot,
        JsonGenerationTaskStore Store,
        GenerationTask Task,
        string CurrentImageId,
        string CurrentImagePath,
        string CurrentImageSha256);

    private sealed class RecordingFallbackService : IImageGenerationCliFallbackService
    {
        internal ImageGenerationCliFallbackRequest? Request { get; private set; }

        public Task<ImageGenerationCliFallbackResult> GenerateAsync(
            ImageGenerationCliFallbackRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            throw new InvalidOperationException("fixture stops after capturing fallback contract");
        }
    }
}
