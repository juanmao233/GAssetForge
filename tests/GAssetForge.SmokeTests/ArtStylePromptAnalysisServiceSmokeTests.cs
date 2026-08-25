using GAssetForge.App.Services;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GAssetForge.SmokeTests;

internal static class ArtStylePromptAnalysisServiceSmokeTests
{
    public static async Task RunAllAsync(string temporaryRoot)
    {
        await SuccessfulTurnUsesFileProtocolAndReturnsValidatedResultAsync(temporaryRoot);
        await HashMismatchFailsBeforeAgentTurnAsync(temporaryRoot);
        await InvalidResultIdentityIsRejectedAndDiagnosticsRemainAsync(temporaryRoot);
        await FailedTurnPreservesRequestAndTurnDiagnosticsAsync(temporaryRoot);
    }

    private static async Task SuccessfulTurnUsesFileProtocolAndReturnsValidatedResultAsync(
        string temporaryRoot)
    {
        var fixture = CreateFixture(temporaryRoot, "success");
        var client = new FakeAgentClient(fixture.WorkspaceRoot, "Codex");
        client.Handler = async (prompt, cancellationToken) =>
        {
            Contains(prompt, "逐张调用当前 Host 可用的视觉查看工具", "分析指令必须要求逐张视觉检查");
            Contains(prompt, "不可信素材数据", "分析指令必须把图片文字视为不可信数据");
            Contains(prompt, "禁止扫描、枚举或搜索其它目录", "分析指令必须禁止扫描其它目录");
            Contains(prompt, "禁止调用图像生成模型", "分析指令必须禁止生成或修改图片");

            var requestPath = FindOnlyRequestFile(fixture.WorkspaceRoot);
            using var request = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath, cancellationToken));
            var root = request.RootElement;
            Equal("已有的人工像素风格说明", root.GetProperty("existingPrompt").GetString(),
                "请求快照必须包含人工提示词");
            Equal(2, root.GetProperty("references").GetArrayLength(),
                "请求快照必须列出所有参考图片");
            var resultPath = root.GetProperty("resultFilePath").GetString()
                ?? throw new InvalidOperationException("请求缺少 resultFilePath。");
            Contains(prompt, resultPath, "Agent 指令必须给出唯一结果文件绝对路径");

            await WriteResultAsync(
                resultPath,
                root.GetProperty("requestId").GetString()!,
                root.GetProperty("groupId").GetString()!,
                root.GetProperty("sourceFingerprint").GetString()!,
                "Codex",
                "codex-test-1",
                cancellationToken);
            return new ArtStylePromptAgentTurnResult(
                "completed",
                ThreadId: "thread-style-test",
                TurnId: "turn-style-test");
        };

        var service = new AgentArtStylePromptAnalysisService(client);
        var result = await service.AnalyzeAsync(fixture.Input);

        Equal("统一的低饱和像素游戏美术风格，清晰轮廓，有限色板。", result.UnifiedPrompt,
            "服务必须返回严格校验后的统一提示词");
        Equal(fixture.Input.GroupId, result.GroupId, "结果必须绑定原风格组");
        Equal(fixture.Input.SourceFingerprint, result.SourceFingerprint,
            "结果必须绑定请求源指纹");
        True(File.Exists(result.RequestFilePath), "request.v1.json 必须保留用于诊断");
        True(File.Exists(result.ResultFilePath), "result.v1.json 必须保留用于复用和诊断");
        True(File.Exists(Path.Combine(result.DiagnosticsDirectory, "turn.v1.json")),
            "成功 Turn 也必须留下结构化诊断");
        True(IsWithin(fixture.WorkspaceRoot, result.RequestFilePath),
            "请求协议必须位于 Agent workspaceWrite 根内");
        Contains(
            result.RequestFilePath.Replace('\\', '/'),
            "/.gassetforge/style-analysis/",
            "请求协议必须使用固定 .gassetforge/style-analysis 目录");
    }

    private static async Task HashMismatchFailsBeforeAgentTurnAsync(string temporaryRoot)
    {
        var fixture = CreateFixture(temporaryRoot, "hash-mismatch");
        var first = fixture.Input.References[0] with { Sha256 = new string('0', 64) };
        var input = fixture.Input with
        {
            References = [first, fixture.Input.References[1]]
        };
        var client = new FakeAgentClient(fixture.WorkspaceRoot, "Codex");
        var service = new AgentArtStylePromptAnalysisService(client);

        await ThrowsAsync<InvalidDataException>(() => service.AnalyzeAsync(input));
        Equal(0, client.RunTurnCallCount, "图片哈希不匹配时不得启动 Agent Turn");
    }

    private static async Task InvalidResultIdentityIsRejectedAndDiagnosticsRemainAsync(
        string temporaryRoot)
    {
        var fixture = CreateFixture(temporaryRoot, "invalid-result");
        var client = new FakeAgentClient(fixture.WorkspaceRoot, "Codex");
        client.Handler = async (_, cancellationToken) =>
        {
            var requestPath = FindOnlyRequestFile(fixture.WorkspaceRoot);
            using var request = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath, cancellationToken));
            var root = request.RootElement;
            await WriteResultAsync(
                root.GetProperty("resultFilePath").GetString()!,
                root.GetProperty("requestId").GetString()!,
                root.GetProperty("groupId").GetString()!,
                new string('f', 64),
                "Codex",
                "codex-test-1",
                cancellationToken);
            return new ArtStylePromptAgentTurnResult("completed");
        };
        var service = new AgentArtStylePromptAnalysisService(client);

        await ThrowsAsync<InvalidDataException>(() => service.AnalyzeAsync(fixture.Input));
        var requestDirectory = Path.GetDirectoryName(FindOnlyRequestFile(fixture.WorkspaceRoot))!;
        True(File.Exists(Path.Combine(requestDirectory, "result.v1.json")),
            "无效结果也必须保留用于诊断");
        True(File.Exists(Path.Combine(requestDirectory, "diagnostics", "failure.v1.json")),
            "结果身份校验失败必须写入诊断记录");
    }

    private static async Task FailedTurnPreservesRequestAndTurnDiagnosticsAsync(
        string temporaryRoot)
    {
        var fixture = CreateFixture(temporaryRoot, "failed-turn");
        var client = new FakeAgentClient(fixture.WorkspaceRoot, "Codex")
        {
            Handler = (_, _) => Task.FromResult(
                new ArtStylePromptAgentTurnResult(
                    "failed",
                    TurnId: "turn-failed",
                    ErrorMessage: "vision tool unavailable"))
        };
        var service = new AgentArtStylePromptAnalysisService(client);

        await ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync(fixture.Input));
        var requestDirectory = Path.GetDirectoryName(FindOnlyRequestFile(fixture.WorkspaceRoot))!;
        True(File.Exists(Path.Combine(requestDirectory, "request.v1.json")),
            "失败 Turn 不能删除请求合同");
        True(File.Exists(Path.Combine(requestDirectory, "diagnostics", "turn.v1.json")),
            "失败 Turn 必须保留 Turn 诊断");
        True(File.Exists(Path.Combine(requestDirectory, "diagnostics", "failure.v1.json")),
            "失败 Turn 必须保留服务失败诊断");
    }

    private static Fixture CreateFixture(string temporaryRoot, string name)
    {
        var root = Path.Combine(
            temporaryRoot,
            "ArtStylePromptAnalysisService",
            name,
            Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var images = Path.Combine(root, "images");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(images);
        var firstPath = Path.Combine(images, "first.png");
        var secondPath = Path.Combine(images, "second.png");
        File.WriteAllBytes(firstPath, CreatePngLikeBytes(1));
        File.WriteAllBytes(secondPath, CreatePngLikeBytes(2));
        var input = new ArtStylePromptAnalysisInput
        {
            GroupId = "11111111111111111111111111111111",
            GroupName = "像素轨道风格",
            Description = "低饱和、工业结构、清晰像素轮廓。",
            ExistingPrompt = "已有的人工像素风格说明",
            SourceFingerprint = ComputeSha256(Encoding.UTF8.GetBytes($"source-{name}")),
            References =
            [
                new ArtStylePromptAnalysisReference
                {
                    Id = "22222222222222222222222222222222",
                    Name = "参考一",
                    SourcePath = firstPath,
                    Sha256 = ComputeFileSha256(firstPath),
                    MediaType = "image/png"
                },
                new ArtStylePromptAnalysisReference
                {
                    Id = "33333333333333333333333333333333",
                    Name = "参考二",
                    SourcePath = secondPath,
                    Sha256 = ComputeFileSha256(secondPath),
                    MediaType = "image/png"
                }
            ]
        };
        return new Fixture(workspace, input);
    }

    private static async Task WriteResultAsync(
        string resultPath,
        string requestId,
        string groupId,
        string fingerprint,
        string analyzer,
        string analyzerVersion,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                requestId,
                groupId,
                sourceFingerprint = fingerprint,
                unifiedPrompt = "统一的低饱和像素游戏美术风格，清晰轮廓，有限色板。",
                summary = "两张参考图共同使用有限色板、硬边像素轮廓和低频工业纹理。",
                analyzer,
                analyzerVersion,
                analyzedAtUtc = DateTimeOffset.UtcNow
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(
            resultPath,
            json,
            new UTF8Encoding(false),
            cancellationToken);
    }

    private static string FindOnlyRequestFile(string workspaceRoot)
    {
        var analysisRoot = Path.Combine(workspaceRoot, ".gassetforge", "style-analysis");
        var paths = Directory.GetFiles(analysisRoot, "request.v1.json", SearchOption.AllDirectories);
        if (paths.Length != 1)
        {
            throw new InvalidOperationException($"预期唯一 request.v1.json，实际 {paths.Length} 个。");
        }

        return paths[0];
    }

    private static byte[] CreatePngLikeBytes(byte marker) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        marker, 0x47, 0x41, 0x53, 0x53, 0x45, 0x54
    ];

    private static string ComputeFileSha256(string path) =>
        ComputeSha256(File.ReadAllBytes(path));

    private static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action)
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

        throw new InvalidOperationException($"预期抛出 {typeof(TException).Name}。");
    }

    private static void Contains(string actual, string expected, string message)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{message}：未包含“{expected}”。");
        }
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

    private sealed record Fixture(
        string WorkspaceRoot,
        ArtStylePromptAnalysisInput Input);

    private sealed class FakeAgentClient(string workingDirectory, string displayName)
        : IArtStylePromptAgentClient
    {
        public bool IsConnected { get; set; } = true;

        public string DisplayName { get; } = displayName;

        public string WorkingDirectory { get; } = workingDirectory;

        public int RunTurnCallCount { get; private set; }

        public Func<string, CancellationToken, Task<ArtStylePromptAgentTurnResult>>? Handler { get; set; }

        public Task<ArtStylePromptAgentTurnResult> RunTurnAsync(
            string prompt,
            CancellationToken cancellationToken = default)
        {
            RunTurnCallCount++;
            return Handler?.Invoke(prompt, cancellationToken) ??
                   throw new InvalidOperationException("测试 Agent 不应收到 Turn。");
        }
    }
}
