using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.ArtStyles;
using System.IO;

namespace GAssetForge.SmokeTests;

internal static class ArtStylePromptSampleSmokeTests
{
    public static void Run()
    {
        FingerprintUsesOnlyNormalizedPromptSources();
        LegacyVersionOneLibraryRemainsValidWithoutSample();
        FreshSamplesRequireExactCoverageWhileStaleSamplesRemainRecoverable();
        ViewModelAppliesPersistsAndInvalidatesSampleAtomically();
    }

    private static void FingerprintUsesOnlyNormalizedPromptSources()
    {
        var firstAsset = CreateAsset('a', '1', "image/png");
        var secondAsset = CreateAsset('b', '2', "image/webp");
        var first = new ArtStyleGroupModel
        {
            Id = new string('c', 32),
            Name = "组名不会进入指纹",
            Description = "  low saturation\r\n stone  ",
            Prompt = "pixel   art",
            UnifiedPromptSample = new ArtStylePromptSampleModel
            {
                Prompt = "也不会进入指纹"
            },
            Assets = [secondAsset, firstAsset]
        };
        var second = new ArtStyleGroupModel
        {
            Id = first.Id,
            Name = "已重命名",
            Description = "low saturation stone",
            Prompt = " pixel art ",
            Assets = [firstAsset, secondAsset]
        };

        var firstFingerprint = ArtStylePromptFingerprint.Compute(first);
        var secondFingerprint = ArtStylePromptFingerprint.Compute(second);
        Equal(firstFingerprint, secondFingerprint,
            "指纹应忽略组名、统一样本、素材顺序和无意义空白");

        second.Assets[0].MediaType = "image/jpeg";
        NotEqual(firstFingerprint, ArtStylePromptFingerprint.Compute(second),
            "素材媒体类型变化必须令统一提示词缓存过期");
    }

    private static void LegacyVersionOneLibraryRemainsValidWithoutSample()
    {
        var legacy = ArtStyleLibraryFactory.CreateDefault();
        var json = ArtStyleJson.Serialize(legacy);
        False(json.Contains("unifiedPromptSample", StringComparison.Ordinal),
            "空统一提示词样本不应写入旧版兼容 JSON");

        var restored = ArtStyleJson.Deserialize(json);
        ArtStyleLibraryValidator.Validate(restored);
        True(restored.Groups.Single().UnifiedPromptSample is null,
            "旧 v1 清单缺少新字段时应恢复为空样本");
    }

    private static void FreshSamplesRequireExactCoverageWhileStaleSamplesRemainRecoverable()
    {
        var firstAsset = CreateAsset('a', '1', "image/png");
        var secondAsset = CreateAsset('b', '2', "image/png");
        var now = DateTimeOffset.UtcNow;
        var group = new ArtStyleGroupModel
        {
            Id = new string('c', 32),
            Name = "coverage-test",
            Description = "pixel art",
            Prompt = "limited palette",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Assets = [firstAsset, secondAsset]
        };
        var freshFingerprint = ArtStylePromptFingerprint.Compute(group);
        group.UnifiedPromptSample = new ArtStylePromptSampleModel
        {
            Prompt = "cohesive limited-palette pixel art",
            Summary = "coverage smoke",
            SourceFingerprint = freshFingerprint,
            GeneratedAtUtc = now,
            AnalyzerId = "codex",
            AnalyzerVersion = "smoke-v1",
            RequestId = Guid.NewGuid().ToString("N"),
            AnalyzedAssetIds = [firstAsset.Id],
            AnalyzedAssetCount = 1
        };

        Throws<InvalidDataException>(
            () => ArtStyleLibraryValidator.Validate(CreateLibrary(group)),
            "标记为新鲜的样本必须精确覆盖当前组全部素材");

        group.UnifiedPromptSample.SourceFingerprint = new string('f', 64);
        ArtStyleLibraryValidator.Validate(CreateLibrary(group));

        group.Assets.Remove(firstAsset);
        ArtStyleLibraryValidator.Validate(CreateLibrary(group));
        True(
            group.UnifiedPromptSample.AnalyzedAssetIds.Contains(firstAsset.Id),
            "素材变化后应保留旧样本的历史素材 ID 供诊断，而不是阻止素材库保存");
    }

    private static void ViewModelAppliesPersistsAndInvalidatesSampleAtomically()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.StylePromptSampleSmokeTests",
            Guid.NewGuid().ToString("N"));
        var paths = new WorkspacePaths(root);
        try
        {
            var library = new ArtStyleLibraryViewModel(paths);
            var group = library.SelectedGroup
                ?? throw new InvalidOperationException("统一提示词 smoke 缺少默认风格组。");
            group.Description = "低饱和石材与清晰像素轮廓";
            group.StylePrompt = "pixel art, readable stone shapes";
            var generatedAt = DateTimeOffset.UtcNow;
            var input = new ArtStylePromptSampleModel
            {
                Prompt = "cohesive low-saturation pixel-art stone, crisp readable silhouettes",
                Summary = "低饱和像素石材，强调清晰轮廓。",
                SourceFingerprint = group.UnifiedPromptSourceFingerprint,
                GeneratedAtUtc = generatedAt,
                AnalyzerId = "codex",
                AnalyzerVersion = "smoke-v1",
                RequestId = Guid.NewGuid().ToString("N"),
                AnalyzedAssetIds = [],
                AnalyzedAssetCount = 0
            };

            True(group.ApplyUnifiedPromptSample(input), "有效统一提示词样本应原子应用");
            True(group.HasUnifiedPrompt && group.IsUnifiedPromptFresh,
                "刚应用的统一提示词样本应可用且新鲜");
            Equal(input.Prompt, group.UnifiedPromptText, "统一提示词正文");
            Equal(input.Summary, group.UnifiedPromptSummary, "统一提示词摘要");
            Equal(generatedAt, group.UnifiedPromptGeneratedAtUtc,
                "统一提示词生成时间");

            input.Prompt = "调用方后续修改不得污染已应用样本";
            NotEqual(input.Prompt, group.UnifiedPromptText,
                "ViewModel 应保存分析结果的防御性副本");

            library.Shutdown();
            var restored = new ArtStyleLibraryViewModel(paths);
            try
            {
                var restoredGroup = restored.SelectedGroup
                    ?? throw new InvalidOperationException("重载后缺少风格组。");
                True(restoredGroup.HasUnifiedPrompt && restoredGroup.IsUnifiedPromptFresh,
                    "统一提示词样本应随 v1 风格库持久化");
                var previousPrompt = restoredGroup.UnifiedPromptText;

                var staleResult = new ArtStylePromptSampleModel
                {
                    Prompt = "不应覆盖当前缓存",
                    Summary = string.Empty,
                    SourceFingerprint = new string('0', 64),
                    GeneratedAtUtc = DateTimeOffset.UtcNow,
                    AnalyzerId = "codex",
                    AnalyzerVersion = "smoke-v1",
                    RequestId = Guid.NewGuid().ToString("N"),
                    AnalyzedAssetIds = [],
                    AnalyzedAssetCount = 0
                };
                False(restoredGroup.ApplyUnifiedPromptSample(staleResult),
                    "来源指纹不匹配的异步结果必须被拒绝");
                Equal(previousPrompt, restoredGroup.UnifiedPromptText,
                    "拒绝旧结果时必须保留此前完整样本");

                restoredGroup.Description += "，加入暖色边缘光";
                True(restoredGroup.HasUnifiedPrompt && !restoredGroup.IsUnifiedPromptFresh,
                    "人工描述变化后应保留旧样本但标记为过期");
                True(restoredGroup.UnifiedPromptStateText.Contains("重新分析", StringComparison.Ordinal),
                    "过期状态应给出重新分析提示");
            }
            finally
            {
                restored.Shutdown();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ArtStyleAssetModel CreateAsset(char idCharacter, char hashCharacter, string mediaType) =>
        new()
        {
            Id = new string(idCharacter, 32),
            Name = $"asset-{idCharacter}",
            OriginalFileName = $"asset-{idCharacter}.png",
            RelativePath = $"assets/{new string('c', 32)}/{new string(idCharacter, 32)}.png",
            Extension = ".png",
            MediaType = mediaType,
            Sha256 = new string(hashCharacter, 64),
            Size = 1,
            ImportedAtUtc = DateTimeOffset.UtcNow
        };

    private static ArtStyleLibraryModel CreateLibrary(ArtStyleGroupModel group) =>
        new()
        {
            SchemaVersion = ArtStyleLibraryValidator.CurrentSchemaVersion,
            Groups = [group],
            SelectedGroupId = group.Id
        };

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

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected={expected}; Actual={actual}");
        }
    }

    private static void NotEqual<T>(T first, T second, string message)
    {
        if (EqualityComparer<T>.Default.Equals(first, second))
        {
            throw new InvalidOperationException(message);
        }
    }
}
