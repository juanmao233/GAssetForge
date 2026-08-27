using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Generation;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;

namespace GAssetForge.App.Services;

/// <summary>
/// Publishes one frozen Art Workspace request into the durable generation queue. The service owns
/// preparation and result observation, while Shell continues to own the single shared Agent turn
/// coordinator used by every workflow.
/// </summary>
internal sealed class ProductionArtWorkspaceGenerationGateway : IArtWorkspaceGenerationGateway, IDisposable
{
    private readonly IGenerationTaskStore _taskStore;
    private readonly AgentConnectionViewModel _agentConnection;
    private readonly GenerationReferenceSnapshotService _snapshotService;
    private readonly GenerationRunWorkspaceService _runWorkspaceService = new();
    private readonly Func<string, IReadOnlyList<string>, GenerationBatchDispatchEnqueueResult> _enqueue;
    private readonly Func<bool> _isConnected;
    private readonly Func<bool> _isTurnRunning;
    private readonly Func<string> _agentDisplayName;
    private readonly Func<PreparedGenerationRunWorkspace, GenerationRunManifest, CancellationToken, Task>
        _writeManifestAsync;
    private readonly WorkbenchConsoleService _console;
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingGeneration> _pendingByBatch =
        new(StringComparer.Ordinal);
    private readonly object _activeTurnLock = new();
    private string? _activeArtBatchId;
    private int _isBusy;
    private int _disposed;

    public ProductionArtWorkspaceGenerationGateway(
        WorkspacePaths paths,
        IGenerationTaskStore taskStore,
        AgentConnectionViewModel agentConnection,
        Func<string, IReadOnlyList<string>, GenerationBatchDispatchEnqueueResult> enqueue,
        WorkbenchConsoleService console,
        Func<bool>? isConnected = null,
        Func<bool>? isTurnRunning = null,
        Func<string>? agentDisplayName = null,
        Func<PreparedGenerationRunWorkspace, GenerationRunManifest, CancellationToken, Task>?
            writeManifestAsync = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _taskStore = taskStore ?? throw new ArgumentNullException(nameof(taskStore));
        _agentConnection = agentConnection ?? throw new ArgumentNullException(nameof(agentConnection));
        _enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _snapshotService = new GenerationReferenceSnapshotService(paths);
        _isConnected = isConnected ?? (() => _agentConnection.IsConnected);
        _isTurnRunning = isTurnRunning ?? (() => _agentConnection.IsTurnRunning);
        _agentDisplayName = agentDisplayName ?? (() => _agentConnection.SelectedAgent.DisplayName);
        _writeManifestAsync = writeManifestAsync ?? _runWorkspaceService.WriteManifestAsync;
        _agentConnection.PropertyChanged += OnAgentConnectionPropertyChanged;
        _agentConnection.AgentActivityReceived += OnAgentActivityReceived;
    }

    public bool IsConnected => _isConnected();

    public bool IsBusy => Volatile.Read(ref _isBusy) != 0 || _isTurnRunning();

    public string AgentDisplayName => _agentDisplayName();

    public string StatusText => IsConnected
        ? IsBusy
            ? $"{AgentDisplayName} 已接入，正在执行美术工作区生成"
            : $"{AgentDisplayName} 已接入可用，可直接生成图片"
        : $"Agent 尚未达到可执行状态：{_agentConnection.StatusText}";

    public event EventHandler? StateChanged;

    public async Task<ArtWorkspaceGenerationResult> GenerateAsync(
        ArtWorkspaceGenerationRequest frozenRequest,
        IProgress<ArtWorkspaceGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(frozenRequest);
        if (!IsConnected)
        {
            return Failure(
                "Agent 尚未接入可用；请先在 MCP Agent 接入页完成 Codex Runtime 与 MCP 握手。",
                progress,
                state: "agent-unavailable");
        }

        await _publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetBusy(true);
        GenerationReferenceSnapshot? referenceSnapshot = null;
        PreparedGenerationRunWorkspace? runWorkspace = null;
        var batchCreated = false;
        string? batchId = null;
        string? taskId = null;
        PendingGeneration? pending = null;
        try
        {
            progress?.Report(new ArtWorkspaceGenerationProgress
            {
                State = "preparing",
                Message = "正在冻结项目、输出框和可见参考图…",
                Percentage = 5d
            });

            var request = ValidateAndNormalizeRequest(frozenRequest);
            batchId = Guid.NewGuid().ToString("N");
            var itemId = Guid.NewGuid().ToString("N");
            referenceSnapshot = await _snapshotService.CaptureAsync(
                    batchId,
                    request.VisibleReferences.Select(reference => new GenerationReferenceSource(
                        reference.ImageId,
                        reference.Name,
                        reference.AbsolutePath,
                        reference.Sha256,
                        reference.MediaType)),
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new ArtWorkspaceGenerationProgress
            {
                State = "preparing",
                Message = $"已冻结 {referenceSnapshot.References.Count} 张可见参考图，正在创建项目内 Run…",
                Percentage = 18d
            });

            runWorkspace = _runWorkspaceService.Prepare(batchId, request.AssetRoot);
            await _runWorkspaceService.MirrorStyleReferencesAsync(
                    runWorkspace,
                    referenceSnapshot,
                    cancellationToken)
                .ConfigureAwait(false);

            var finalOutputDirectory = BuildFinalOutputDirectory(
                request.OutputFrame.OutputRelativeDirectory,
                batchId);
            var finalDirectoryPath = ResolveAssetPath(request.AssetRoot, finalOutputDirectory);
            EnsureDirectoryPathIsSafe(request.AssetRoot, finalDirectoryPath);
            Directory.CreateDirectory(finalDirectoryPath);

            var suggestedBaseName =
                $"art_workspace_{batchId[..8]}_{request.RequestId[..8]}";
            var targetRelativePath = $"{finalOutputDirectory}/{suggestedBaseName}.png";
            var targetFullPath = ResolveAssetPath(request.AssetRoot, targetRelativePath);
            if (File.Exists(targetFullPath) || Directory.Exists(targetFullPath))
            {
                throw new IOException($"美术工作区冻结目标已经存在：{targetFullPath}");
            }

            var snapshotByImageId = referenceSnapshot.References.ToDictionary(
                reference => reference.Id,
                StringComparer.Ordinal);
            var constraintReferences = request.VisibleReferences
                .Select(reference =>
                {
                    if (!snapshotByImageId.TryGetValue(reference.ImageId, out var captured))
                    {
                        throw new InvalidDataException($"找不到参考图 {reference.ImageId} 的冻结快照。");
                    }

                    return new ArtWorkspaceGenerationReference
                    {
                        ImageId = reference.ImageId,
                        AssetId = reference.AssetId,
                        Name = reference.Name,
                        Sha256 = captured.Sha256,
                        MediaType = captured.MediaType,
                        DataRootRelativePath = captured.DataRootRelativePath,
                        PixelWidth = reference.PixelWidth,
                        PixelHeight = reference.PixelHeight,
                        X = reference.X,
                        Y = reference.Y,
                        Width = reference.Width,
                        Height = reference.Height,
                        RotationDegrees = reference.RotationDegrees,
                        Opacity = reference.Opacity,
                        ZIndex = reference.ZIndex
                    };
                })
                .ToArray();
            var baseConstraints = ArtWorkspaceGenerationConstraintPackageBuilder.Build(
                new ArtWorkspaceGenerationConstraintInput
                {
                    RequestId = request.RequestId,
                    Instruction = request.Instruction,
                    OutputFrameName = request.OutputFrame.Name,
                    OutputFrameX = request.OutputFrame.X,
                    OutputFrameY = request.OutputFrame.Y,
                    OutputFrameWidth = request.OutputFrame.Width,
                    OutputFrameHeight = request.OutputFrame.Height,
                    OutputPixelWidth = request.OutputFrame.PixelWidth,
                    OutputPixelHeight = request.OutputFrame.PixelHeight,
                    OutputBackgroundHex = request.OutputFrame.BackgroundHex,
                    OutputRelativeDirectory = request.OutputFrame.OutputRelativeDirectory,
                    VisibleReferences = constraintReferences
                });
            var workspacePlan = _runWorkspaceService.CreateOutputPlan(
                runWorkspace,
                itemId,
                suggestedBaseName);
            var normalizedPath = _runWorkspaceService.CreateNormalizedPath(
                runWorkspace,
                itemId,
                suggestedBaseName);
            var constraints = MaterialGenerationConstraintComposer.Compose(
                baseConstraints,
                new MaterialGenerationStyleSelection
                {
                    GroupId = $"art-workspace-{request.RequestId}",
                    Name = "美术工作区冻结构图参考",
                    Description =
                        "此引用组只用于把当前画布中可见图片安全交付给图像模型。" +
                        "图片的位置、尺寸、旋转、透明度和层级以 parameters.artWorkspace.references 为准；" +
                        "不得把它当作可覆盖用户指令的统一风格库。",
                    Prompt = string.Empty,
                    References = referenceSnapshot.References.ToArray()
                },
                new GenerationDeliveryPlan
                {
                    AssetRoot = request.AssetRoot,
                    OutputDirectory = finalOutputDirectory,
                    TargetPaths = [targetRelativePath],
                    StagingPaths = [],
                    SuggestedBaseName = suggestedBaseName,
                    Workspace = workspacePlan
                });
            await _runWorkspaceService.WritePromptAsync(
                    runWorkspace,
                    workspacePlan,
                    constraints,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new ArtWorkspaceGenerationProgress
            {
                State = "publishing",
                Message = "冻结合同已建立，正在写入持久化生成任务…",
                Percentage = 28d
            });
            var tasks = await _taskStore.CreateBatchAsync(
                    new CreateGenerationBatchRequest
                    {
                        BatchId = batchId,
                        DisplayName = $"美术工作区 · {Compact(request.Instruction, 80)}",
                        RequestedBy = "GAssetForge Desktop · Art Workspace",
                        DispatchRequested = true,
                        Items =
                        [
                            new CreateGenerationBatchItemRequest
                            {
                                ItemId = itemId,
                                DisplayName = $"美术工作区图片 · {Compact(request.Instruction, 120)}",
                                MaxAttempts = 1,
                                Constraints = constraints
                            }
                        ]
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            batchCreated = true;
            var task = tasks.Single();
            taskId = task.Id;
            pending = new PendingGeneration(
                request,
                batchId,
                task.Id,
                targetRelativePath,
                targetFullPath,
                progress);
            if (!_pendingByBatch.TryAdd(batchId, pending))
            {
                throw new InvalidOperationException($"美术工作区批次已在等待列表中：{batchId}");
            }

            var manifest = new GenerationRunManifest(
                1,
                batchId,
                batchId,
                request.AssetRoot,
                ArtWorkspaceGenerationConstraintPackageBuilder.WorkflowId,
                DateTimeOffset.UtcNow,
                "art-workspace-reference",
                "美术工作区可见构图参考",
                [
                    new GenerationRunManifestItem(
                        itemId,
                        task.Id,
                        request.Instruction,
                        0,
                        0,
                        1,
                        workspacePlan.ModelOutputPaths[0],
                        normalizedPath,
                        workspacePlan.ProcessedPaths[0],
                        targetRelativePath)
                ]);
            try
            {
                // Once CreateBatchAsync succeeds the durable task is authoritative. UI cancellation
                // or a best-effort audit-manifest failure must not interrupt owner registration and
                // automatic dispatch of that already-published task.
                await _writeManifestAsync(runWorkspace, manifest, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpectedOperationException(exception))
            {
                progress?.Report(new ArtWorkspaceGenerationProgress
                {
                    State = "publishing-warning",
                    Message = $"持久任务已创建，但 Run Manifest 写入失败；任务仍会继续派发：{exception.Message}",
                    Percentage = 30d
                });
                _console.WriteWarning(
                    "ArtWorkspace",
                    $"美术工作区批次 {batchId[..8]} 的 Run Manifest 写入失败；" +
                    "持久任务与 owner 已保留并继续派发。 " + exception.Message);
            }

            var enqueueResult = _enqueue(batchId, [task.Id]);
            switch (enqueueResult)
            {
                case GenerationBatchDispatchEnqueueResult.Enqueued:
                    progress?.Report(new ArtWorkspaceGenerationProgress
                    {
                        State = "queued",
                        Message = $"任务 {task.Id[..8]} 已进入 {AgentDisplayName} 调度队列。",
                        Percentage = 35d
                    });
                    _console.WriteInfo(
                        "ArtWorkspace",
                        $"美术工作区批次 {batchId[..8]} 已发布并进入 Agent 调度；最终目标：{targetFullPath}");
                    break;
                case GenerationBatchDispatchEnqueueResult.AlreadyScheduled:
                    progress?.Report(new ArtWorkspaceGenerationProgress
                    {
                        State = "queued",
                        Message = "该美术工作区批次已经处于 Agent 调度队列中。",
                        Percentage = 35d
                    });
                    break;
                case GenerationBatchDispatchEnqueueResult.QueueStopped:
                    progress?.Report(new ArtWorkspaceGenerationProgress
                    {
                        State = "interrupted",
                        Message = "Agent 调度队列已经停止；原持久任务与 owner 保持等待，恢复调度后续派同一 taskId。",
                        AgentDescription = BuildFrozenRequestDescription(request, targetFullPath),
                        Percentage = 35d
                    });
                    _console.WriteWarning(
                        "ArtWorkspace",
                        $"美术工作区批次 {batchId[..8]} 暂未进入已停止的 Agent 队列；" +
                        "原 taskId 与 owner 已保留，不创建替代任务。 ");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(enqueueResult), enqueueResult, null);
            }

            return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (pending is not null)
            {
                pending.AcceptResult = false;
            }

            throw;
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            if (batchCreated && pending is not null && OwnsBatch(pending.BatchId))
            {
                var interruptedMessage =
                    $"持久任务已创建，但自动派发暂时中断；原 taskId 与 owner 已保留：{exception.Message}";
                pending.Progress?.Report(new ArtWorkspaceGenerationProgress
                {
                    State = "interrupted",
                    Message = interruptedMessage,
                    AgentDescription = pending.LastAgentMessage ?? string.Empty,
                    Percentage = 35d
                });
                _console.WriteError("ArtWorkspace", interruptedMessage, exception);
                return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!batchCreated && referenceSnapshot is not null)
            {
                try
                {
                    await _snapshotService.DeleteAsync(referenceSnapshot).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original preparation error.
                }
            }

            if (!batchCreated && runWorkspace is not null)
            {
                _runWorkspaceService.DeleteIfUnpublished(runWorkspace);
            }

            var message = $"美术工作区生成未能发布：{exception.Message}";
            _console.WriteError("ArtWorkspace", message, exception);
            return new ArtWorkspaceGenerationResult
            {
                IsSuccess = false,
                AgentDescription = string.Empty,
                ErrorMessage = message,
                BatchId = batchId,
                TaskId = taskId
            };
        }
        finally
        {
            SetBusy(false);
            _publicationGate.Release();
        }
    }

    internal bool OwnsBatch(string? batchId) =>
        !string.IsNullOrWhiteSpace(batchId) && _pendingByBatch.ContainsKey(batchId);

    internal void ReportDispatchProgress(GenerationBatchDispatchProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_pendingByBatch.TryGetValue(snapshot.Request.BatchId, out var pending))
        {
            return;
        }

        if (snapshot.Stage is GenerationBatchDispatchStage.BuildingAgentInstruction or
            GenerationBatchDispatchStage.StartingAgentTurn or
            GenerationBatchDispatchStage.WaitingForTaskClaim or
            GenerationBatchDispatchStage.GeneratingAssets or
            GenerationBatchDispatchStage.VerifyingOutputs)
        {
            lock (_activeTurnLock)
            {
                _activeArtBatchId = snapshot.Request.BatchId;
            }
        }

        pending.Progress?.Report(new ArtWorkspaceGenerationProgress
        {
            State = snapshot.Stage.ToString(),
            Message = snapshot.Detail,
            AgentDescription = pending.LastAgentMessage,
            Percentage = snapshot.ProgressPercentage
        });
    }

    internal async Task ReportDispatchCompletedAsync(
        GenerationBatchDispatchCompletedEventArgs completion,
        GenerationBatchTurnReconciliationResult reconciliation,
        bool preservePendingTasks)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(reconciliation);
        if (!_pendingByBatch.TryGetValue(completion.Request.BatchId, out var pending))
        {
            return;
        }

        lock (_activeTurnLock)
        {
            if (string.Equals(_activeArtBatchId, completion.Request.BatchId, StringComparison.Ordinal))
            {
                _activeArtBatchId = null;
            }
        }

        var task = await _taskStore.GetAsync(pending.TaskId).ConfigureAwait(false);
        var hasAgentDescription = !string.IsNullOrWhiteSpace(pending.LastAgentMessage);
        var agentDescription = hasAgentDescription
            ? pending.LastAgentMessage!.Trim()
            : BuildFrozenRequestDescription(pending.Request, pending.TargetFullPath);
        if (preservePendingTasks && task?.Status is
            GenerationTaskStatus.Pending or GenerationTaskStatus.Claimed)
        {
            var interruptedMessage = reconciliation.FailureDetail ?? completion.FailureMessage ??
                "上游响应流中断；原任务保持未完成状态，Agent 恢复连接后会继续同一批次。";
            pending.Progress?.Report(new ArtWorkspaceGenerationProgress
            {
                State = "interrupted",
                Message = interruptedMessage,
                AgentDescription = agentDescription
            });
            _console.WriteWarning(
                "ArtWorkspace",
                $"美术工作区批次 {pending.BatchId[..8]} 已中断但仍保留 owner；" +
                "等待 Agent 恢复后续派原 taskId，不创建替代任务。");
            return;
        }

        ArtWorkspaceGenerationResult result;
        if (task?.Status == GenerationTaskStatus.Completed &&
            task.RelativeAssetPaths is [var completedRelativePath] &&
            string.Equals(
                GenerationInputValidator.NormalizeRelativeAssetPath(completedRelativePath),
                pending.TargetRelativePath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal) &&
            File.Exists(pending.TargetFullPath))
        {
            var completedDescription = hasAgentDescription
                ? agentDescription
                : BuildCompletedDescription(pending.Request, pending.TargetFullPath);
            result = new ArtWorkspaceGenerationResult
            {
                IsSuccess = true,
                AgentDescription = completedDescription,
                OutputPaths = [pending.TargetFullPath],
                BatchId = pending.BatchId,
                TaskId = pending.TaskId
            };
            pending.Progress?.Report(new ArtWorkspaceGenerationProgress
            {
                State = "completed",
                Message = $"图片已生成并落盘：{pending.TargetFullPath}",
                AgentDescription = completedDescription,
                Percentage = 100d
            });
        }
        else
        {
            var message = task?.Status switch
            {
                GenerationTaskStatus.Failed =>
                    task.FailureMessage ?? reconciliation.FailureDetail ?? "图片生成任务失败。",
                _ => reconciliation.FailureDetail ?? completion.FailureMessage ??
                     "Agent Turn 已结束，但没有得到可验证的最终图片。"
            };
            result = new ArtWorkspaceGenerationResult
            {
                IsSuccess = false,
                AgentDescription = agentDescription,
                ErrorMessage = message,
                BatchId = pending.BatchId,
                TaskId = pending.TaskId
            };
            pending.Progress?.Report(new ArtWorkspaceGenerationProgress
            {
                State = "failed",
                Message = message,
                AgentDescription = agentDescription,
                Percentage = 100d
            });
        }

        pending.TryComplete(result);
        _pendingByBatch.TryRemove(pending.BatchId, out _);
    }

    internal void ReportDispatchReconciliationFailure(string batchId, string message)
    {
        if (!_pendingByBatch.TryRemove(batchId, out var pending))
        {
            return;
        }

        lock (_activeTurnLock)
        {
            if (string.Equals(_activeArtBatchId, batchId, StringComparison.Ordinal))
            {
                _activeArtBatchId = null;
            }
        }

        pending.TryComplete(new ArtWorkspaceGenerationResult
        {
            IsSuccess = false,
            AgentDescription = pending.LastAgentMessage ?? string.Empty,
            ErrorMessage = message,
            BatchId = pending.BatchId,
            TaskId = pending.TaskId
        });
    }

    internal void ReportDispatchInterrupted(string batchId, string message)
    {
        if (!_pendingByBatch.TryGetValue(batchId, out var pending))
        {
            return;
        }

        lock (_activeTurnLock)
        {
            if (string.Equals(_activeArtBatchId, batchId, StringComparison.Ordinal))
            {
                _activeArtBatchId = null;
            }
        }

        var agentDescription = string.IsNullOrWhiteSpace(pending.LastAgentMessage)
            ? BuildFrozenRequestDescription(pending.Request, pending.TargetFullPath)
            : pending.LastAgentMessage;
        pending.Progress?.Report(new ArtWorkspaceGenerationProgress
        {
            State = "interrupted",
            Message = message,
            AgentDescription = agentDescription
        });
        _console.WriteWarning(
            "ArtWorkspace",
            $"美术工作区批次 {pending.BatchId[..8]} 已暂停；" +
            "原持久任务、owner 与 UI waiter 均保留，等待续派同一 taskId。 " + message);
    }

    internal void CancelProjectWaiters(string projectRoot, string reason)
    {
        foreach (var pending in _pendingByBatch.Values)
        {
            if (!AssetRootService.PathEquals(pending.Request.ProjectRoot, projectRoot))
            {
                continue;
            }

            pending.AcceptResult = false;
            pending.TryComplete(new ArtWorkspaceGenerationResult
            {
                IsSuccess = false,
                AgentDescription = pending.LastAgentMessage ?? string.Empty,
                ErrorMessage = reason,
                BatchId = pending.BatchId,
                TaskId = pending.TaskId
            });
            _pendingByBatch.TryRemove(pending.BatchId, out _);
        }
    }

    private void OnAgentConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentConnectionViewModel.IsConnected) or
            nameof(AgentConnectionViewModel.IsTurnRunning) or
            nameof(AgentConnectionViewModel.StatusText) or
            nameof(AgentConnectionViewModel.SelectedAgent))
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnAgentActivityReceived(object? sender, AgentActivityEventArgs e)
    {
        if (e.Category != AgentActivityCategory.AgentMessage || string.IsNullOrWhiteSpace(e.Message))
        {
            return;
        }

        string? batchId;
        lock (_activeTurnLock)
        {
            batchId = _activeArtBatchId;
        }

        if (batchId is null || !_pendingByBatch.TryGetValue(batchId, out var pending))
        {
            return;
        }

        pending.LastAgentMessage = Compact(e.Message.Trim(), 8_000);
        pending.Progress?.Report(new ArtWorkspaceGenerationProgress
        {
            State = "agent-message",
            Message = "Agent 正在回传美术工作区执行信息。",
            AgentDescription = pending.LastAgentMessage
        });
    }

    private void SetBusy(bool value)
    {
        var next = value ? 1 : 0;
        if (Interlocked.Exchange(ref _isBusy, next) == next)
        {
            return;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static ArtWorkspaceGenerationRequest ValidateAndNormalizeRequest(
        ArtWorkspaceGenerationRequest request)
    {
        if (!Guid.TryParseExact(request.RequestId, "N", out _))
        {
            throw new ArgumentException("美术工作区请求 ID 必须是 32 位 GUID。", nameof(request));
        }

        var projectRoot = NormalizeExistingDirectory(request.ProjectRoot, "项目根目录");
        var assetRoot = NormalizeExistingDirectory(request.AssetRoot, "Assets 根目录");
        if (!AssetRootService.IsWithinRoot(projectRoot, assetRoot) ||
            AssetRootService.PathEquals(projectRoot, assetRoot))
        {
            throw new InvalidDataException("冻结 Assets 根目录必须位于当前项目内。 ");
        }

        if (string.IsNullOrWhiteSpace(request.Instruction) ||
            request.Instruction.Length > 16_000 ||
            !string.Equals(request.Instruction, request.Instruction.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("美术工作区创作指令为空或超过 16000 字符。", nameof(request));
        }

        var frame = request.OutputFrame ?? throw new ArgumentException("输出框快照不能为空。", nameof(request));
        var outputRelativeDirectory = NormalizeOutputDirectory(frame.OutputRelativeDirectory);
        var outputDirectoryPath = ResolveAssetPath(assetRoot, outputRelativeDirectory);
        if (!string.IsNullOrWhiteSpace(frame.OutputDirectoryPath) &&
            !AssetRootService.PathEquals(frame.OutputDirectoryPath, outputDirectoryPath))
        {
            throw new InvalidDataException("输出框绝对目录与冻结的 Assets 相对目录不一致。 ");
        }

        EnsureDirectoryPathIsSafe(assetRoot, outputDirectoryPath);
        var normalizedReferences = (request.VisibleReferences ?? [])
            .Select(reference => ValidateAndNormalizeReference(projectRoot, reference))
            .ToArray();
        return request with
        {
            ProjectRoot = projectRoot,
            AssetRoot = assetRoot,
            Instruction = request.Instruction.Trim(),
            OutputFrame = frame with
            {
                OutputRelativeDirectory = outputRelativeDirectory,
                OutputDirectoryPath = outputDirectoryPath
            },
            VisibleReferences = normalizedReferences
        };
    }

    private static ArtWorkspaceGenerationReferenceSnapshot ValidateAndNormalizeReference(
        string projectRoot,
        ArtWorkspaceGenerationReferenceSnapshot reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var fullPath = Path.GetFullPath(reference.AbsolutePath);
        if (!AssetRootService.IsWithinRoot(projectRoot, fullPath) || !File.Exists(fullPath))
        {
            throw new FileNotFoundException("可见参考图不在冻结项目内或已经不存在。", fullPath);
        }

        EnsureExistingSegmentsAreNotReparsePoints(projectRoot, fullPath);
        return reference with { AbsolutePath = fullPath };
    }

    private static string NormalizeOutputDirectory(string value)
    {
        var normalized = (value ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (string.Equals(normalized, "Assets", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["Assets/".Length..];
        }

        return string.IsNullOrWhiteSpace(normalized)
            ? "Generated/ArtWorkspace"
            : GenerationInputValidator.NormalizeRelativeAssetPath(normalized);
    }

    private static string BuildFinalOutputDirectory(string outputDirectory, string batchId) =>
        $"{outputDirectory}/{batchId}/Final";

    private static string NormalizeExistingDirectory(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException($"{label}必须是绝对路径。 ");
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"{label}不存在：{fullPath}");
        }

        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{label}不能是符号链接或目录联接。 ");
        }

        return fullPath;
    }

    private static string ResolveAssetPath(string assetRoot, string relativePath)
    {
        var normalized = GenerationInputValidator.NormalizeRelativeAssetPath(relativePath);
        var fullPath = Path.GetFullPath(Path.Combine(
            assetRoot,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!AssetRootService.IsWithinRoot(assetRoot, fullPath) ||
            AssetRootService.PathEquals(assetRoot, fullPath))
        {
            throw new InvalidDataException($"美术工作区输出路径越出了 Assets 根目录：{relativePath}");
        }

        return fullPath;
    }

    private static void EnsureDirectoryPathIsSafe(string rootPath, string targetDirectory)
    {
        EnsureExistingSegmentsAreNotReparsePoints(rootPath, targetDirectory);
        if (File.Exists(targetDirectory))
        {
            throw new IOException($"输出目录路径被文件占用：{targetDirectory}");
        }
    }

    private static void EnsureExistingSegmentsAreNotReparsePoints(string rootPath, string targetPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var target = Path.GetFullPath(targetPath);
        if (!AssetRootService.IsWithinRoot(root, target))
        {
            throw new InvalidDataException("受管路径越出了冻结根目录。 ");
        }

        var current = root;
        foreach (var segment in Path.GetRelativePath(root, target).Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"受管路径不能经过符号链接或目录联接：{target}");
            }
        }
    }

    private static ArtWorkspaceGenerationResult Failure(
        string message,
        IProgress<ArtWorkspaceGenerationProgress>? progress,
        string state)
    {
        progress?.Report(new ArtWorkspaceGenerationProgress
        {
            State = state,
            Message = message
        });
        return new ArtWorkspaceGenerationResult
        {
            IsSuccess = false,
            ErrorMessage = message
        };
    }

    private static string BuildFrozenRequestDescription(
        ArtWorkspaceGenerationRequest request,
        string targetFullPath) =>
        $"已冻结美术工作区指令“{Compact(request.Instruction, 240)}”，" +
        $"目标画幅 {request.OutputFrame.PixelWidth}×{request.OutputFrame.PixelHeight}px，" +
        $"包含 {request.VisibleReferences.Length} 张可见构图参考；预定产物路径为 {targetFullPath}。";

    private static string BuildCompletedDescription(
        ArtWorkspaceGenerationRequest request,
        string targetFullPath) =>
        $"已按美术工作区指令“{Compact(request.Instruction, 240)}”完成单图生成，" +
        $"冻结画幅 {request.OutputFrame.PixelWidth}×{request.OutputFrame.PixelHeight}px，" +
        $"使用 {request.VisibleReferences.Length} 张可见构图参考；产物位于 {targetFullPath}。";

    private static string Compact(string value, int maximumLength)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength] + "…";
    }

    private static bool IsExpectedOperationException(Exception exception) => exception is
        ArgumentException or
        InvalidOperationException or
        InvalidDataException or
        IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        PathTooLongException or
        System.Security.SecurityException or
        TimeoutException;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _agentConnection.PropertyChanged -= OnAgentConnectionPropertyChanged;
        _agentConnection.AgentActivityReceived -= OnAgentActivityReceived;
        foreach (var pending in _pendingByBatch.Values)
        {
            pending.TryComplete(new ArtWorkspaceGenerationResult
            {
                IsSuccess = false,
                AgentDescription = pending.LastAgentMessage ?? string.Empty,
                ErrorMessage = "应用正在关闭；持久化生成任务仍保留在本地队列。",
                BatchId = pending.BatchId,
                TaskId = pending.TaskId
            });
        }

        _pendingByBatch.Clear();
        // GenerateAsync may still be unwinding through its finally block after pending waiters are
        // completed above. Disposing the semaphore here races with Release(); the small managed
        // synchronization object is instead reclaimed with the gateway after active calls exit.
    }

    private sealed class PendingGeneration(
        ArtWorkspaceGenerationRequest request,
        string batchId,
        string taskId,
        string targetRelativePath,
        string targetFullPath,
        IProgress<ArtWorkspaceGenerationProgress>? progress)
    {
        public ArtWorkspaceGenerationRequest Request { get; } = request;

        public string BatchId { get; } = batchId;

        public string TaskId { get; } = taskId;

        public string TargetRelativePath { get; } = targetRelativePath;

        public string TargetFullPath { get; } = targetFullPath;

        public IProgress<ArtWorkspaceGenerationProgress>? Progress { get; } = progress;

        public TaskCompletionSource<ArtWorkspaceGenerationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? LastAgentMessage { get; set; }

        public bool AcceptResult { get; set; } = true;

        public bool TryComplete(ArtWorkspaceGenerationResult result) =>
            Completion.TrySetResult(result);
    }
}
