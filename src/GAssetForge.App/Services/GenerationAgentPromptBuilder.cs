using System.Text;
using GAssetForge.Core;

namespace GAssetForge.App.Services;

public static class GenerationAgentPromptBuilder
{
    public const string McpServerName = ProductIdentity.McpServerName;

    public static string Build(IReadOnlyCollection<string> taskIds) => BuildCore(taskIds, null);

    internal static string Build(
        IReadOnlyCollection<string> taskIds,
        ImageDeliveryPreflightResult preflight) => BuildCore(taskIds, preflight);

    private static string BuildCore(
        IReadOnlyCollection<string> taskIds,
        ImageDeliveryPreflightResult? preflight)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        var normalizedIds = taskIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedIds.Length == 0)
        {
            throw new ArgumentException("至少需要一个有效的生成任务 ID。", nameof(taskIds));
        }

        var prompt = new StringBuilder();
        prompt.AppendLine("执行 GAssetForge 已发布的游戏美术素材生成批次。");
        prompt.AppendLine($"默认只使用名为 `{McpServerName}` 的 MCP Server 读取并更新这些任务；只有提示明确提供 GAssetForge 官方本地任务桥接 CLI 时，才允许通过该桥接调用同名工具：");
        foreach (var taskId in normalizedIds)
        {
            prompt.Append("- ").AppendLine(taskId);
        }

        prompt.AppendLine();
        prompt.AppendLine("这些 taskId 已按一张图片一个任务拆分。任务间并发和多 Agent 分配交给当前 Agent Host 默认调度；不要读取页面并行数，也不要创建或复用固定命名的子 Agent 槽位。");
        if (preflight is not null)
        {
            prompt.Append("应用启动前图片本地交付预检：")
                .AppendLine(SingleLine(preflight.StatusText));
            if (preflight.CliFallbackLocalPrerequisitesReady)
            {
                prompt.Append("CLI 本地前置已验证，但 Image API 网络未验证；Python：")
                    .Append(SingleLine(preflight.PythonExecutablePath ?? "python"))
                    .Append("；官方脚本：")
                    .AppendLine(SingleLine(preflight.ImageGenScriptPath ?? "scripts/image_gen.py"));
            }
        }

        prompt.AppendLine("对每个任务只执行不可替代的生成步骤；单项失败不得阻止其余任务：");
        prompt.AppendLine("1. 使用 claim_generation_task(taskId, agentId) 读取冻结的 prompt、风格参考、背景色合同、delivery.workspace，以及软件计算好的项目内绝对 recommendedSourcePath。不要另建 generation task。");
        prompt.AppendLine("2. 若任务已有有效最终 target，submit_generation_output 会幂等返回。若精确 recommendedSourcePath 已存在且是可读的有效 PNG，说明本任务可能在模型落盘后断连；必须先直接以该精确路径调用 submit_generation_output，成功后不得再次生成。软件预检会把发现的损坏文件安全隔离；若 claim 后该精确路径又出现无效文件，停止该项并报告明确诊断，禁止覆盖或使用 --force。无需用 PowerShell 或其它命令扫描目录。");
        prompt.AppendLine("3. 按 preferredSkill 调用真实图像生成能力。preferredSkill=imagegen 时立即使用当前 Codex Host 的内置 image_gen/ImageGen。仅在本次内置调用返回一个明确的新绝对本地 PNG 路径，且该文件存在、可读时，才视为取得可提交模型产物；只出现可视图片但没有新本地路径不算完成。严禁扫描、按时间挑选或复用 `~/.codex/generated_images` 中的历史 PNG。若内置调用没有返回明确本地路径，立即使用官方 imagegen skill 的 scripts/image_gen.py CLI。CLI 兜底必须使用预检给出的绝对 Python 与 image_gen.py 路径（若上方已给出），不得重新通过可能陈旧的 PATH 解析 python、py 或脚本。CLI 必须完整使用 claim.imageGeneration：把 imageGeneration.prompt、negativePrompt、requiredRules、style.description 和 style.prompt 合并为提示；显式保留 imageGeneration.output.targetWidth、imageGeneration.output.targetHeight 与 imageGeneration.output.targetAspectRatio 描述的最终构图，并传入 `--size <imageGeneration.output.cliSize>`，不得默认改成方形。imageGeneration.style.referenceSourcePaths 非空时使用 `edit --image <path>`，对每张冻结参考图重复 `--image`；无参考图时才使用 generate。通过 `--out <recommendedSourcePath>` 直接写入软件预建路径；不得用 uv 临时下载 Python。CLI 本地前置就绪不代表 Image API 可达；网络未验证，必须保留真实认证、配额、socket 或 HTTP 错误。不得用 PowerShell、System.Drawing、Pillow、SVG、Canvas 或程序化图形代替模型生成。生成提示必须完整保留 required perspective rules，尤其是 axisA/axisB、两条边的绝对屏幕角度及禁止额外旋转条款；不得把控制角 90°、旋转 0°改写成 diamond/rhombus、on-point square 或角点朝上的 45° 方形。生成提示还必须明确使用 output.backgroundRemoval 指定的纯色底。");
        prompt.AppendLine("4. 只有确认本次 sourcePath 文件存在、可读且具有有效 PNG 签名后，才直接调用 submit_generation_output(taskId, agentId, outputIndex=0, sourcePath=<模型返回的绝对路径>)；CLI 兜底时 sourcePath 必须精确等于 recommendedSourcePath。GAssetForge 会自行把原图导入项目运行工作区并归档，并完成尺寸/画布归一化、补齐首道底色、背景剔除、约束校验、最终原子提交和任务完成登记。Agent 不得自行建目录、搬运、缩放、抠图、修改 Alpha 或调用 process_generation_output/complete_generation_task 重复施工；只允许检查本次内置调用明确返回的 sourcePath 或 claim 返回的精确 recommendedSourcePath，不得遍历其它图片目录。");
        prompt.AppendLine("5. 只有 imagegen 或 submit_generation_output 真实失败时才调用 fail_generation_task，记录工具返回的准确错误；不得把 Claimed 当成 Completed。若内置工具只有可视结果且 CLI 无法交付，failureMessage 必须以 `image_delivery_fallback_unavailable:` 开头并准确区分 Python/py、image_gen.py、OPENAI_API_KEY、输出目录这些本地前置，以及网络未验证后实际返回的 DNS、socket、TLS、HTTP、认证、配额或 API 错误，不得误报为本地生成脚本缺失。");
        prompt.AppendLine("兼容说明：若 claim 返回的旧任务没有 delivery.workspace，才按旧合同使用 delivery.stagingPaths、process_generation_output 和 complete_generation_task；不要把旧流程套到新任务。");
        prompt.AppendLine("完成后只汇总每个 taskId 的成功/失败和最终 targetPath；不要报告 Codex 默认目录中的临时文件为正式产物。");
        return prompt.ToString();
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
