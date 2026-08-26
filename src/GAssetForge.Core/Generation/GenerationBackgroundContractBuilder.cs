using System.Text.Json.Nodes;

namespace GAssetForge.Core.Generation;

internal static class GenerationBackgroundContractBuilder
{
    public const string SolidOpaqueColorMode = "solid-opaque-color";
    public const string TransparentAlphaMode = "transparent-alpha";

    public static string BuildInstruction(
        GenerationBackgroundRemovalSpec? backgroundRemoval,
        string transparentRule,
        string subjectLabel) =>
        backgroundRemoval is null
            ? transparentRule
            : $"生成阶段必须使用均匀纯色 {backgroundRemoval.ColorHex} 背景，画布边缘和{subjectLabel}外均保持该颜色；" +
              "不得使用渐变、纹理、棋盘格、背景投影或环境雾污染底色，也不要自行抠图或制造透明通道。" +
              "GAssetForge 会把该原图作为第一道工序输入，在本地剔除底色并交付最终真实透明 PNG；" +
              $"最终{subjectLabel}外 alpha 必须为 0。";

    public static JsonObject BuildParameters(
        GenerationBackgroundRemovalSpec? backgroundRemoval,
        string transparentAreaPropertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transparentAreaPropertyName);

        var generationStage = backgroundRemoval is null
            ? new JsonObject
            {
                ["mode"] = TransparentAlphaMode,
                [transparentAreaPropertyName] = 0
            }
            : new JsonObject
            {
                ["mode"] = SolidOpaqueColorMode,
                ["colorHex"] = backgroundRemoval.ColorHex,
                ["alpha"] = 255
            };

        return new JsonObject
        {
            ["generationStage"] = generationStage,
            ["finalStage"] = new JsonObject
            {
                ["mode"] = TransparentAlphaMode,
                [transparentAreaPropertyName] = 0
            }
        };
    }
}
