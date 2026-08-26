using System.IO;
using System.Text.Json.Nodes;
using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.Core.Generation;
using GAssetForge.McpServer;

namespace GAssetForge.SmokeTests;

internal static class LeaseRegressionTests
{
    public static void HttpSessionsExpireAfterInactivityDeterministically()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 8, 20, 7, 0, 0, TimeSpan.Zero));
        var sessions = new McpSessionRegistry(time, TimeSpan.FromSeconds(90));
        var session = sessions.CreateSession();
        session.RecordInitialize("2025-06-18", "Codex Lease Tests", "1.0.0");
        session.CompleteInitialization();
        var sessionId = sessions.Add(session);

        Equal(1, sessions.GetActiveSessions().Length, "握手会话应进入活跃集合");
        Equal(true, sessions.GetActiveSessions().Any(candidate => candidate.IsInitialized),
            "活跃握手会话应令健康状态可判定为 connected");

        time.Advance(TimeSpan.FromSeconds(89));
        Equal(true, sessions.TryGetActive(sessionId, out _), "TTL 内的请求应续期会话活动时间");

        time.Advance(TimeSpan.FromSeconds(89));
        Equal(1, sessions.GetActiveSessions().Length, "请求续期后的会话不应提前过期");

        time.Advance(TimeSpan.FromSeconds(2));
        Equal(0, sessions.GetActiveSessions().Length, "健康检查路径应清理超过 TTL 的会话");
        Equal(false, sessions.TryGetActive(sessionId, out _), "过期会话不得被请求路径重新激活");

        var requestExpiredSession = sessions.CreateSession();
        requestExpiredSession.RecordInitialize("2025-06-18", "Codex Lease Tests", "1.0.0");
        requestExpiredSession.CompleteInitialization();
        var requestExpiredSessionId = sessions.Add(requestExpiredSession);
        time.Advance(TimeSpan.FromSeconds(91));

        Equal(false, sessions.TryGetActive(requestExpiredSessionId, out _),
            "请求路径本身也应清理超时会话");
        Equal(0, sessions.Count, "无活跃握手会话时健康状态应可回到 waiting");
    }

    public static async Task GenerationClaimsExpireAndReturnToPendingAsync()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "generation-claim-lease",
            Guid.NewGuid().ToString("N"));
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero));

        try
        {
            var store = new JsonGenerationTaskStore(testRoot, time);
            var created = await store.CreateAsync(CreateRequest("租约回收测试"));
            var claimed = await store.ClaimAsync(created.Id, "codex:lease-test");

            Equal(GenerationTaskStatus.Claimed, claimed.Status, "任务应先进入 Claimed");
            Equal(2, claimed.Revision, "首次认领修订号");

            time.Advance(TimeSpan.FromMinutes(31));
            var recovered = await store.GetAsync(created.Id)
                ?? throw new InvalidOperationException("租约测试任务丢失。");

            Equal(GenerationTaskStatus.Pending, recovered.Status, "过期认领应恢复为 Pending");
            Equal(3, recovered.Revision, "租约回收应推进修订号");
            Equal(time.GetUtcNow(), recovered.UpdatedAtUtc, "租约回收应推进更新时间");
            Equal<string?>(null, recovered.ClaimedBy, "租约回收应清空 Agent");
            Equal<DateTimeOffset?>(null, recovered.ClaimedAtUtc, "租约回收应清空认领时间");

            await ThrowsAsync<InvalidOperationException>(
                () => store.CompleteAsync(created.Id, "codex:lease-test", ["Generated/expired.png"]),
                "过期的原 Agent 不得直接完成任务");

            var reclaimed = await store.ClaimAsync(created.Id, "opencode:lease-test");
            Equal(4, reclaimed.Revision, "回收后的重新认领应再次推进修订号");
            await ThrowsAsync<InvalidOperationException>(
                () => store.CompleteAsync(created.Id, "codex:lease-test", ["Generated/stale-owner.png"]),
                "旧 Agent 不得结束已被其他 Agent 重新认领的任务");
            var completed = await store.CompleteAsync(
                created.Id,
                "opencode:lease-test",
                ["Generated/reclaimed.png"]);
            Equal(GenerationTaskStatus.Completed, completed.Status, "新认领者应可正常完成任务");

            var directExpiry = await store.CreateAsync(CreateRequest("完成入口回收测试"));
            await store.ClaimAsync(directExpiry.Id, "codex:direct-expiry");
            time.Advance(TimeSpan.FromMinutes(31));
            await ThrowsAsync<InvalidOperationException>(
                () => store.CompleteAsync(
                    directExpiry.Id,
                    "codex:direct-expiry",
                    ["Generated/direct-expired.png"]),
                "Complete 入口必须先回收过期租约再拒绝旧认领者");
            var recoveredByComplete = await store.GetAsync(directExpiry.Id)
                ?? throw new InvalidOperationException("完成入口回收测试任务丢失。");
            Equal(GenerationTaskStatus.Pending, recoveredByComplete.Status,
                "Complete 入口拒绝旧 Agent 后应持久化 Pending 回收结果");
            Equal(3, recoveredByComplete.Revision, "Complete 入口回收修订号");

            var claimedAfterRecovery = await store.ClaimNextAsync("opencode:after-expiry");
            Equal(directExpiry.Id, claimedAfterRecovery?.Id, "ClaimNext 应能立即重新认领已回收任务");
            Equal(4, claimedAfterRecovery?.Revision, "ClaimNext 重新认领修订号");

            var listExpiry = await store.CreateAsync(CreateRequest("列表入口回收测试"));
            await store.ClaimAsync(listExpiry.Id, "codex:list-expiry");
            time.Advance(TimeSpan.FromMinutes(31));
            var pendingTasks = await store.ListAsync(GenerationTaskStatus.Pending);
            var recoveredByList = pendingTasks.SingleOrDefault(task => task.Id == listExpiry.Id)
                ?? throw new InvalidOperationException("List(Pending) 未返回已过期回收的任务。");
            Equal(3, recoveredByList.Revision, "List 入口回收修订号");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    public static async Task GenerationClaimHeartbeatsExtendOnlyTheActiveOwnerLeaseAsync()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "generation-claim-heartbeat",
            Guid.NewGuid().ToString("N"));
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));
        var lease = TimeSpan.FromMinutes(10);

        try
        {
            var store = new JsonGenerationTaskStore(testRoot, time, lease);
            var created = await store.CreateAsync(CreateRequest("长任务续租测试"));
            var claimed = await store.ClaimAsync(created.Id, "codex:heartbeat-owner");
            var originalClaimedAt = claimed.ClaimedAtUtc;

            time.Advance(TimeSpan.FromMinutes(6));
            var firstHeartbeat = await store.RenewClaimAsync(created.Id, "codex:heartbeat-owner");
            Equal(GenerationTaskStatus.Claimed, firstHeartbeat.Status, "合法心跳后任务必须保持 Claimed");
            Equal(3, firstHeartbeat.Revision, "首次心跳必须推进任务修订号");
            Equal(originalClaimedAt, firstHeartbeat.ClaimedAtUtc, "心跳不得覆盖首次认领时间");
            Equal(time.GetUtcNow(), firstHeartbeat.UpdatedAtUtc, "心跳必须推进租约活动时间");

            time.Advance(TimeSpan.FromMinutes(6));
            var activePastOriginalLease = await store.GetAsync(created.Id)
                ?? throw new InvalidOperationException("续租任务丢失。");
            Equal(GenerationTaskStatus.Claimed, activePastOriginalLease.Status,
                "超过首次认领租约边界但心跳仍有效时不得回收任务");

            var secondHeartbeat = await store.RenewClaimAsync(created.Id, "codex:heartbeat-owner");
            Equal(4, secondHeartbeat.Revision, "第二次心跳必须继续推进修订号");
            var completed = await store.CompleteAsync(
                created.Id,
                "codex:heartbeat-owner",
                ["Generated/heartbeat-completed.png"]);
            Equal(GenerationTaskStatus.Completed, completed.Status,
                "持续心跳的原 Agent 必须能在首次租约边界后完成任务");

            var unauthorized = await store.CreateAsync(CreateRequest("错误 Agent 心跳测试"));
            await store.ClaimAsync(unauthorized.Id, "codex:rightful-owner");
            time.Advance(TimeSpan.FromMinutes(6));
            await ThrowsAsync<InvalidOperationException>(
                () => store.RenewClaimAsync(unauthorized.Id, "codex:wrong-owner"),
                "其它 Agent 不得替当前所有者续租");
            time.Advance(TimeSpan.FromMinutes(5));
            var recoveredAfterUnauthorizedHeartbeat = await store.GetAsync(unauthorized.Id)
                ?? throw new InvalidOperationException("错误 Agent 心跳测试任务丢失。");
            Equal(GenerationTaskStatus.Pending, recoveredAfterUnauthorizedHeartbeat.Status,
                "未授权心跳不得阻止原认领按期回收");

            var alreadyExpired = await store.CreateAsync(CreateRequest("过期后心跳测试"));
            await store.ClaimAsync(alreadyExpired.Id, "codex:expired-owner");
            time.Advance(TimeSpan.FromMinutes(11));
            await ThrowsAsync<InvalidOperationException>(
                () => store.RenewClaimAsync(alreadyExpired.Id, "codex:expired-owner"),
                "租约已经过期后旧 Agent 不得通过迟到心跳复活所有权");
            var expiredAfterHeartbeat = await store.GetAsync(alreadyExpired.Id)
                ?? throw new InvalidOperationException("过期后心跳测试任务丢失。");
            Equal(GenerationTaskStatus.Pending, expiredAfterHeartbeat.Status,
                "迟到心跳被拒绝后任务必须保持可重新认领的 Pending");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    public static async Task ActiveGenerationTurnRenewsClaimsAndStopsAfterTurnAsync()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "generation-dispatch-heartbeat",
            Guid.NewGuid().ToString("N"));
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        var lease = TimeSpan.FromMinutes(10);

        try
        {
            var store = new JsonGenerationTaskStore(testRoot, time, lease);
            var activeTask = await store.CreateAsync(CreateRequest("活动 Turn 自动续租"));
            var heartbeatGate = new ManualHeartbeatGate();
            var turnClaimed = new TaskCompletionSource<GenerationTask>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var finishTurn = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatchCompleted = new TaskCompletionSource<GenerationBatchDispatchCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            await using (var coordinator = new GenerationBatchDispatchCoordinator(
                             async (_, cancellationToken) =>
                             {
                                 var claimed = await store.ClaimAsync(
                                     activeTask.Id,
                                     "codex:active-turn",
                                     cancellationToken);
                                 turnClaimed.TrySetResult(claimed);
                                 await finishTurn.Task.WaitAsync(cancellationToken);
                                 return new AgentTurnResult(
                                     ThreadId: "thread-heartbeat",
                                     TurnId: "turn-heartbeat",
                                     Status: "completed",
                                     ErrorMessage: null);
                             },
                             store,
                             claimHeartbeatInterval: TimeSpan.FromMinutes(2),
                             heartbeatDelay: heartbeatGate.WaitAsync))
            {
                coordinator.DispatchCompleted += (_, eventArgs) => dispatchCompleted.TrySetResult(eventArgs);
                Equal(GenerationBatchDispatchEnqueueResult.Enqueued,
                    coordinator.TryEnqueue("batch-heartbeat", [activeTask.Id], isRetry: false),
                    "长任务批次必须进入调度队列");
                var initialClaim = await turnClaimed.Task.WaitAsync(TimeSpan.FromSeconds(3));

                time.Advance(TimeSpan.FromMinutes(6));
                heartbeatGate.Pulse();
                await WaitForRevisionAsync(store, activeTask.Id, initialClaim.Revision + 1);
                time.Advance(TimeSpan.FromMinutes(6));
                heartbeatGate.Pulse();
                var renewed = await WaitForRevisionAsync(store, activeTask.Id, initialClaim.Revision + 2);

                Equal(GenerationTaskStatus.Claimed, renewed.Status,
                    "活动 Turn 跨过首次租约边界后任务必须仍为 Claimed");
                Equal("codex:active-turn", renewed.ClaimedBy,
                    "调度续租不得改变任务所有者");
                var completed = await store.CompleteAsync(
                    activeTask.Id,
                    "codex:active-turn",
                    ["Generated/active-turn-completed.png"]);
                Equal(GenerationTaskStatus.Completed, completed.Status,
                    "自动续租后原 Agent 必须能正常完成任务");

                finishTurn.TrySetResult();
                var result = await dispatchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Equal(true, result.IsSuccess, "完成长任务后调度结果必须成功");
            }

            var abandonedTask = await store.CreateAsync(CreateRequest("Turn 结束后停止续租"));
            var abandonedDispatchCompleted = new TaskCompletionSource<GenerationBatchDispatchCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var noHeartbeatGate = new ManualHeartbeatGate();
            await using (var coordinator = new GenerationBatchDispatchCoordinator(
                             async (_, cancellationToken) =>
                             {
                                 await store.ClaimAsync(
                                     abandonedTask.Id,
                                     "codex:ended-turn",
                                     cancellationToken);
                                 return new AgentTurnResult(
                                     ThreadId: "thread-ended",
                                     TurnId: "turn-ended",
                                     Status: "completed",
                                     ErrorMessage: null);
                             },
                             store,
                             claimHeartbeatInterval: TimeSpan.FromMinutes(2),
                             heartbeatDelay: noHeartbeatGate.WaitAsync))
            {
                coordinator.DispatchCompleted += (_, eventArgs) =>
                    abandonedDispatchCompleted.TrySetResult(eventArgs);
                Equal(GenerationBatchDispatchEnqueueResult.Enqueued,
                    coordinator.TryEnqueue("batch-ended", [abandonedTask.Id], isRetry: false),
                    "结束续租测试批次必须进入调度队列");
                await abandonedDispatchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }

            time.Advance(TimeSpan.FromMinutes(11));
            var recovered = await store.GetAsync(abandonedTask.Id)
                ?? throw new InvalidOperationException("Turn 结束后停止续租任务丢失。");
            Equal(GenerationTaskStatus.Pending, recovered.Status,
                "Turn 结束后调度器必须停止心跳，使遗留 Claimed 任务仍能按原租约回收");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static CreateGenerationTaskRequest CreateRequest(string displayName) => new()
    {
        DisplayName = displayName,
        RequestedBy = "smoke-tests",
        Constraints = new GenerationConstraintPackage
        {
            WorkflowId = "tilemap",
            Prompt = "lease regression",
            Output = new GenerationOutputSpec
            {
                Width = 64,
                Height = 64,
                Count = 1,
                Format = "png",
                TransparentBackground = true
            },
            Parameters = new JsonObject()
        }
    };

    private static async Task ThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static async Task<GenerationTask> WaitForRevisionAsync(
        IGenerationTaskStore store,
        string taskId,
        int expectedRevision)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(3);
        GenerationTask? latest = null;
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            latest = await store.GetAsync(taskId);
            if (latest is not null && latest.Revision >= expectedRevision)
            {
                return latest;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException(
            $"等待任务续租修订号 {expectedRevision} 超时；最后修订号 {latest?.Revision.ToString() ?? "missing"}。");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    internal sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc.ToUniversalTime();

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration));
            }

            _utcNow = _utcNow.Add(duration);
        }
    }

    private sealed class ManualHeartbeatGate
    {
        private readonly SemaphoreSlim _pulses = new(0);

        internal Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            if (interval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(interval));
            }

            return _pulses.WaitAsync(cancellationToken);
        }

        internal void Pulse() => _pulses.Release();
    }
}
