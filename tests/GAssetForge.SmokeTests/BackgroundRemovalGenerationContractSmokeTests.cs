using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using GAssetForge.Core.Generation;
using GAssetForge.McpServer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace GAssetForge.SmokeTests;

internal static class BackgroundRemovalGenerationContractSmokeTests
{
    public static void Run()
    {
        LegacyOutputWithoutBackgroundRemovalValidatesAndRoundTrips();
        ComposeAllocatesIndexAlignedStagingPaths();
        LegacyStagingDirectoryRemainsReadable();
        InvalidBackgroundRemovalContractsAreRejected();
        BackgroundRemovalDecodedBudgetIsEnforcedAtTaskCreation();
        ReplaceDeliveryReallocatesStagingPaths();
        TilemapPromptRequiresSolidBackgroundAndLocalRemoval();
        TilemapBackgroundStagesAreExplicitAndLegacyParametersRemainValid();
    }

    private static void LegacyOutputWithoutBackgroundRemovalValidatesAndRoundTrips()
    {
        var legacyConstraints = TilemapConstraintPackageBuilder.Build(new TilemapGenerationConstraintInput
        {
            ArtWidth = 128,
            ArtHeight = 64,
            ContentPrompt = "旧版无背景剔除 Tilemap 契约"
        });

        Equal<GenerationBackgroundRemovalSpec?>(
            null,
            legacyConstraints.Output.BackgroundRemoval,
            "旧版 output 应允许缺少 BackgroundRemoval");
        GenerationInputValidator.ValidateConstraintPackage(legacyConstraints);

        var createdAt = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var legacyTask = new GenerationTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Revision = 1,
            DisplayName = "旧版背景契约往返",
            Status = GenerationTaskStatus.Pending,
            Constraints = legacyConstraints,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt
        };

        var json = GenerationJson.SerializeTask(legacyTask);
        False(
            json.Contains("\"backgroundRemoval\"", StringComparison.Ordinal),
            "旧版任务 JSON 不应被强制写入空 BackgroundRemoval");
        var restored = GenerationJson.DeserializeTask(json);
        Equal<GenerationBackgroundRemovalSpec?>(
            null,
            restored.Constraints.Output.BackgroundRemoval,
            "旧版任务往返后 BackgroundRemoval 应保持为空");
        Equal(legacyConstraints.Output.Width, restored.Constraints.Output.Width, "旧版 output 宽度往返");
        Equal(legacyConstraints.Output.Height, restored.Constraints.Output.Height, "旧版 output 高度往返");
        GenerationInputValidator.ValidateConstraintPackage(restored.Constraints);
    }

    private static void ComposeAllocatesIndexAlignedStagingPaths()
    {
        var assetRoot = CreateAssetRoot("compose");
        string[] targets =
        [
            "Generated/Tilemap/ground-001.png",
            "Generated/Tilemap/ground-002.png"
        ];
        var composed = MaterialGenerationConstraintComposer.Compose(
            CreateTilemapPackage(targets.Length),
            CreateStyle(),
            new GenerationDeliveryPlan
            {
                AssetRoot = assetRoot,
                OutputDirectory = "Generated/Tilemap",
                TargetPaths = targets,
                SuggestedBaseName = "ground"
            });

        True(
            MaterialGenerationConstraintComposer.TryReadDelivery(composed, out var delivery),
            "启用背景剔除的 Compose 结果应包含可读取的 delivery");
        var resolved = delivery
            ?? throw new InvalidOperationException("Compose 未返回背景剔除 delivery。");
        Equal(targets.Length, resolved.StagingPaths.Length, "stagingPaths 应与 targetPaths 数量一致");
        Equal(
            "Generated/Tilemap/.gassetforge-staging/ground-001.pfraw.png",
            resolved.StagingPaths[0],
            "第一个 stagingPath 应由第一个 targetPath 稳定派生");
        Equal(
            "Generated/Tilemap/.gassetforge-staging/ground-002.pfraw.png",
            resolved.StagingPaths[1],
            "第二个 stagingPath 应由第二个 targetPath 稳定派生");
        Equal(2, resolved.StagingPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "自动分配的 stagingPaths 不得重复");
        for (var index = 0; index < targets.Length; index++)
        {
            False(
                string.Equals(targets[index], resolved.StagingPaths[index], StringComparison.OrdinalIgnoreCase),
                $"索引 {index} 的 stagingPath 不得与最终 targetPath 相同");
            True(
                Path.GetFileNameWithoutExtension(resolved.StagingPaths[index])
                    .StartsWith(Path.GetFileNameWithoutExtension(targets[index]), StringComparison.OrdinalIgnoreCase),
                $"索引 {index} 的 stagingPath 应与对应 targetPath 保持可追溯映射");
        }
    }

    private static void LegacyStagingDirectoryRemainsReadable()
    {
        var target = "Generated/Tilemap/legacy-ground.png";
        var legacyStaging = "Generated/Tilemap/.perspectiveforge-staging/legacy-ground.pfraw.png";
        var composed = MaterialGenerationConstraintComposer.Compose(
            CreateTilemapPackage(1),
            CreateStyle(),
            new GenerationDeliveryPlan
            {
                AssetRoot = CreateAssetRoot("legacy-staging"),
                OutputDirectory = "Generated/Tilemap",
                TargetPaths = [target],
                StagingPaths = [legacyStaging],
                SuggestedBaseName = "legacy-ground"
            });

        True(MaterialGenerationConstraintComposer.TryReadDelivery(composed, out var delivery),
            "旧 staging 合同必须继续可读取");
        Equal(legacyStaging, delivery!.StagingPaths[0],
            "旧 staging 路径不能在恢复历史任务时被重写");
    }

    private static void InvalidBackgroundRemovalContractsAreRejected()
    {
        Throws<ArgumentException>(
            () => TilemapConstraintPackageBuilder.Build(new TilemapGenerationConstraintInput
            {
                BackgroundRemoval = CreateBackgroundRemoval() with
                {
                    ColorHex = "#GG00FF"
                }
            }),
            "非法背景颜色必须被拒绝");

        Throws<ArgumentException>(
            () => TilemapConstraintPackageBuilder.Build(new TilemapGenerationConstraintInput
            {
                BackgroundRemoval = CreateBackgroundRemoval() with
                {
                    TransparentThreshold = 120d,
                    OpaqueThreshold = 30d
                }
            }),
            "不透明阈值不大于透明阈值时必须被拒绝");

        var valid = CreateTilemapPackage(1);
        var nonPng = valid with
        {
            Output = valid.Output with
            {
                Format = "webp"
            }
        };
        Throws<ArgumentException>(
            () => GenerationInputValidator.ValidateConstraintPackage(nonPng),
            "启用背景剔除时非 PNG 最终输出必须被拒绝");
    }

    private static void BackgroundRemovalDecodedBudgetIsEnforcedAtTaskCreation()
    {
        const int width = 16_384;
        const int heightAtLimit = 8_208;
        const int heightAboveLimit = heightAtLimit + 1;
        var basePackage = CreateTilemapPackage(1);
        var package = basePackage with
        {
            WorkflowId = "background-removal-budget-test",
            Parameters = [],
            Output = basePackage.Output with
            {
                Width = width,
                Height = heightAtLimit
            }
        };
        var decodedBytesAtLimit = checked(
            (long)width *
            heightAtLimit *
            GenerationBackgroundRemovalSpec.DecodedRgbaBytesPerPixel);
        Equal(
            GenerationBackgroundRemovalSpec.MaximumDecodedRgbaBytes,
            decodedBytesAtLimit,
            "背景剔除契约边界应与 MCP 的 513 MiB RGBA 解码预算精确一致");

        GenerationInputValidator.ValidateCreateRequest(new CreateGenerationTaskRequest
        {
            DisplayName = "背景剔除预算边界任务",
            Constraints = package
        });

        var aboveLimit = package with
        {
            Output = package.Output with { Height = heightAboveLimit }
        };
        Throws<ArgumentException>(
            () => GenerationInputValidator.ValidateCreateRequest(new CreateGenerationTaskRequest
            {
                DisplayName = "背景剔除预算超限任务",
                Constraints = aboveLimit
            }),
            "超过 MCP RGBA 解码预算的背景剔除任务必须在创建时被拒绝");

        var withoutBackgroundRemoval = aboveLimit with
        {
            Output = aboveLimit.Output with { BackgroundRemoval = null }
        };
        GenerationInputValidator.ValidateCreateRequest(new CreateGenerationTaskRequest
        {
            DisplayName = "非背景剔除大尺寸任务",
            Constraints = withoutBackgroundRemoval
        });
    }

    private static void ReplaceDeliveryReallocatesStagingPaths()
    {
        var assetRoot = CreateAssetRoot("replace");
        var original = MaterialGenerationConstraintComposer.Compose(
            CreateTilemapPackage(2),
            CreateStyle(),
            new GenerationDeliveryPlan
            {
                AssetRoot = assetRoot,
                OutputDirectory = "Generated/Tilemap",
                TargetPaths =
                [
                    "Generated/Tilemap/original-001.png",
                    "Generated/Tilemap/original-002.png"
                ],
                SuggestedBaseName = "original"
            });
        True(
            MaterialGenerationConstraintComposer.TryReadDelivery(original, out var originalDelivery),
            "原 delivery 应可读取");
        var oldPaths = (originalDelivery
                ?? throw new InvalidOperationException("原 delivery 缺失。"))
            .TargetPaths
            .Concat(originalDelivery.StagingPaths)
            .ToArray();

        var replaced = MaterialGenerationConstraintComposer.ReplaceDelivery(
            original,
            new GenerationDeliveryPlan
            {
                AssetRoot = assetRoot,
                OutputDirectory = "Generated/Tilemap/Retry",
                TargetPaths =
                [
                    "Generated/Tilemap/Retry/retry-001.png",
                    "Generated/Tilemap/Retry/retry-002.png"
                ],
                SuggestedBaseName = "retry"
            });

        True(
            MaterialGenerationConstraintComposer.TryReadDelivery(replaced, out var replacedDelivery),
            "ReplaceDelivery 结果应可读取");
        var current = replacedDelivery
            ?? throw new InvalidOperationException("ReplaceDelivery 未生成新 delivery。");
        Equal(
            "Generated/Tilemap/Retry/.gassetforge-staging/retry-001.pfraw.png",
            current.StagingPaths[0],
            "ReplaceDelivery 应为第一张图重新分配 stagingPath");
        Equal(
            "Generated/Tilemap/Retry/.gassetforge-staging/retry-002.pfraw.png",
            current.StagingPaths[1],
            "ReplaceDelivery 应为第二张图重新分配 stagingPath");
        var replacementDeliveryJson = replaced.Parameters["delivery"]?.ToJsonString()
            ?? throw new InvalidOperationException("ReplaceDelivery 缺少 parameters.delivery。");
        foreach (var oldPath in oldPaths)
        {
            False(
                replacementDeliveryJson.Contains(oldPath, StringComparison.OrdinalIgnoreCase),
                $"ReplaceDelivery 不得保留旧路径：{oldPath}");
        }
    }

    private static void TilemapPromptRequiresSolidBackgroundAndLocalRemoval()
    {
        var package = TilemapConstraintPackageBuilder.Build(new TilemapGenerationConstraintInput
        {
            ContentPrompt = "绿色纯色底地表瓦片",
            BackgroundRemoval = CreateBackgroundRemoval() with
            {
                Preset = "green",
                ColorHex = "#00FF00"
            }
        });

        True(
            package.Prompt.Contains("均匀纯色 #00FF00 背景", StringComparison.Ordinal),
            "Tilemap prompt 应冻结并明确第一工序纯色底");
        True(
            package.Prompt.Contains("不要自行抠图或制造透明通道", StringComparison.Ordinal),
            "Tilemap prompt 应阻止 Agent 提前自行抠图");
        True(
            package.Prompt.Contains("在本地剔除底色", StringComparison.Ordinal),
            "Tilemap prompt 应明确由 GAssetForge 本地剔除背景");
        var backgroundRule = package.Rules.Single(rule =>
            string.Equals(rule.Key, "tilemap.background.transparent", StringComparison.Ordinal));
        True(
            backgroundRule.Instruction.Contains("最终真实透明 PNG", StringComparison.Ordinal),
            "Tilemap 背景规则应明确本地处理后的最终交付格式");
    }

    private static void TilemapBackgroundStagesAreExplicitAndLegacyParametersRemainValid()
    {
        var package = TilemapConstraintPackageBuilder.Build(new TilemapGenerationConstraintInput
        {
            ArtWidth = 32,
            ArtHeight = 32,
            DiamondAngle = 90d,
            ContentPrompt = "显式背景工序契约瓦片",
            BackgroundRemoval = CreateBackgroundRemoval()
        });
        var background = package.Parameters["background"] as JsonObject
            ?? throw new InvalidOperationException("Tilemap 契约缺少 parameters.background。");
        False(
            background.ContainsKey("transparent"),
            "新 Tilemap 契约不应继续写入含糊的 parameters.background.transparent");
        var generationStage = background["generationStage"] as JsonObject
            ?? throw new InvalidOperationException("Tilemap 契约缺少 generationStage。");
        Equal(
            TilemapConstraintPackageBuilder.SolidOpaqueColorBackgroundMode,
            generationStage["mode"]?.GetValue<string>(),
            "背景剔除任务的生成工序应明确为纯色不透明底");
        Equal("#FF00FF", generationStage["colorHex"]?.GetValue<string>(),
            "生成工序应冻结背景剔除契约颜色");
        Equal(255, generationStage["alpha"]?.GetValue<int>(),
            "生成工序纯色底应明确为完全不透明");

        var finalStage = background["finalStage"] as JsonObject
            ?? throw new InvalidOperationException("Tilemap 契约缺少 finalStage。");
        Equal(
            TilemapConstraintPackageBuilder.TransparentAlphaBackgroundMode,
            finalStage["mode"]?.GetValue<string>(),
            "最终工序应明确为真实透明 Alpha");
        Equal(0, finalStage["outsideDiamondAlpha"]?.GetValue<int>(),
            "最终工序瓦片有效轮廓外 Alpha 应固定为 0");

        var fixturePath = Path.Combine(
            CreateAssetRoot("background-stages"),
            "explicit-background-stages.png");
        WriteTransparentTilemapFixture(fixturePath, 32, 32);
        GeneratedAssetValidator.Validate(fixturePath, package);

        var legacyParameters = package.Parameters.DeepClone().AsObject();
        legacyParameters["background"] = new JsonObject
        {
            ["transparent"] = true,
            ["outsideDiamondAlpha"] = 0
        };
        GeneratedAssetValidator.Validate(
            fixturePath,
            package with { Parameters = legacyParameters });

        var directAlphaPackage = TilemapConstraintPackageBuilder.Build(
            new TilemapGenerationConstraintInput
            {
                ArtWidth = 32,
                ArtHeight = 32,
                DiamondAngle = 90d,
                ContentPrompt = "直接透明背景工序瓦片"
            });
        var directBackground = directAlphaPackage.Parameters["background"] as JsonObject
            ?? throw new InvalidOperationException("直接透明 Tilemap 契约缺少 background。");
        var directGenerationStage = directBackground["generationStage"] as JsonObject
            ?? throw new InvalidOperationException("直接透明 Tilemap 契约缺少 generationStage。");
        Equal(
            TilemapConstraintPackageBuilder.TransparentAlphaBackgroundMode,
            directGenerationStage["mode"]?.GetValue<string>(),
            "未启用本地背景剔除时生成工序应直接要求真实透明 Alpha");
    }

    private static void WriteTransparentTilemapFixture(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("无法解析 Tilemap 测试图片目录。"));
        using var image = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 0));
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                image[x, y] = new Rgba32(180, 90, 30, 255);
            }
        }
        image.Save(path, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
    }

    private static GenerationConstraintPackage CreateTilemapPackage(int outputCount) =>
        TilemapConstraintPackageBuilder.Build(new TilemapGenerationConstraintInput
        {
            ArtWidth = 128,
            ArtHeight = 64,
            OutputCount = outputCount,
            ContentPrompt = "背景剔除生成契约测试瓦片",
            BackgroundRemoval = CreateBackgroundRemoval()
        });

    private static GenerationBackgroundRemovalSpec CreateBackgroundRemoval() => new()
    {
        Preset = "magenta",
        ColorHex = "#FF00FF",
        TransparentThreshold = 30d,
        OpaqueThreshold = 120d,
        EdgeContract = 1,
        EdgeFeather = 0,
        Despill = true,
        EdgeConnectedOnly = true
    };

    private static MaterialGenerationStyleSelection CreateStyle() => new()
    {
        GroupId = "background-removal-contract-style",
        Name = "背景剔除契约测试风格",
        Description = "用于验证纯色底生成契约。",
        Prompt = "clean game asset"
    };

    private static string CreateAssetRoot(string suffix) => Path.Combine(
        Path.GetTempPath(),
        "GAssetForge.SmokeTests",
        "background-removal-generation-contract",
        suffix,
        Guid.NewGuid().ToString("N"),
        "Assets");

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

    private static void False(bool condition, string message)
    {
        if (condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }
}
