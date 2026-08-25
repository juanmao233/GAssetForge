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
            var handoffState = preflight.State switch
            {
                ImageDeliveryPreflightState.LocalReadyNetworkUnverified =>
                    "GAssetForge Harness 的 Python、官方 image_gen.py、OpenAI SDK、API Key 与项目输出目录本地前置已通过；Image API 网络、认证、配额仍以工具真实结果为准。",
                ImageDeliveryPreflightState.BuiltInOnly =>
                    "GAssetForge Harness CLI 本地前置当前不可用；仍先调用内置 ImageGen，savedPath 为空时必须调用 run_image_delivery_fallback 获取准确诊断，不得自行探测环境。",
                ImageDeliveryPreflightState.OutputUnavailable =>
                    "项目推荐输出目录不可用，当前批次不应继续执行。",
                _ =>
                    "尚未完成 Harness 本地交付预检；以 run_image_delivery_fallback 的结构化结果为准。"
            };
            prompt.Append("图片本地交付状态：").AppendLine(handoffState);
        }

        prompt.AppendLine("对每个任务只执行不可替代的生成步骤；单项失败不得阻止其余任务：");
        prompt.AppendLine("1. 使用 claim_generation_task(taskId, agentId) 读取冻结的 prompt、风格参考、背景色合同、delivery.workspace，以及软件计算好的项目内绝对 recommendedSourcePath。不要另建 generation task。");
        prompt.AppendLine("2. 若任务已有有效最终 target，submit_generation_output 会幂等返回。若精确 recommendedSourcePath 已存在且是可读的有效 PNG，说明本任务可能在模型落盘后断连；必须先直接以该精确路径调用 submit_generation_output，成功后不得再次生成。软件预检会把发现的损坏文件安全隔离；若 claim 后该精确路径又出现无效文件，停止该项并报告明确诊断，禁止覆盖或使用 --force。无需用 PowerShell 或其它命令扫描目录。");
        prompt.AppendLine("3. 按 preferredSkill 调用真实图像生成能力。preferredSkill=imagegen 时立即使用当前 Codex Host 的内置 image_gen/ImageGen。仅当本次调用返回明确的新绝对本地 PNG savedPath/sourcePath，且该文件存在、可读时，才以该路径调用 submit_generation_output。只出现可视图片但 savedPath 为空是协议允许的未落盘状态，不算取得可提交产物；此时必须立即调用 run_image_delivery_fallback(taskId, agentId)。该窄工具会由 GAssetForge Harness 从冻结任务读取完整提示、required rules、风格参考、画幅、纯色底和 recommendedSourcePath，自行发现 Python 与官方 image_gen.py、执行 Image API CLI、验证精确 PNG、完成本地后处理并自动提交任务。Agent 禁止自行运行 python、py、uv 或脚本，严禁扫描、按时间挑选或复用 `~/.codex/generated_images` 中的历史 PNG，也不得用 PowerShell、System.Drawing、Pillow、SVG、Canvas 或程序化图形代替模型生成。");
        prompt.AppendLine("4. 内置 ImageGen 返回明确本地路径时，只有确认该 sourcePath 文件存在、可读且具有有效 PNG 签名后，才调用 submit_generation_output(taskId, agentId, outputIndex=0, sourcePath=<模型返回的绝对路径>)。GAssetForge 会自行把原图导入项目运行工作区并归档，并完成尺寸/画布归一化、补齐首道底色、背景剔除、约束校验、最终原子提交和任务完成登记。Agent 不得自行建目录、搬运、缩放、抠图、修改 Alpha 或调用 process_generation_output/complete_generation_task 重复施工；只允许检查本次内置调用明确返回的 sourcePath 或 claim 返回的精确 recommendedSourcePath，不得遍历其它图片目录。");
        prompt.AppendLine("5. 只有内置 imagegen、run_image_delivery_fallback 或 submit_generation_output 返回真实错误时才调用 fail_generation_task，并记录工具返回的准确错误；不得把 Claimed 当成 Completed。内置工具仅有可视结果且 savedPath 为空时不得直接 fail，必须先调用 Harness 兜底工具。若 Harness 兜底失败，failureMessage 必须以 `image_delivery_fallback_failed:` 开头并保留其对 Python/py、image_gen.py、OPENAI_API_KEY、输出目录、DNS、socket、TLS、HTTP、认证、配额或 API 错误的准确分类。");
        prompt.AppendLine("兼容说明：若 claim 返回的旧任务没有 delivery.workspace，才按旧合同使用 delivery.stagingPaths、process_generation_output 和 complete_generation_task；不要把旧流程套到新任务。");
        prompt.AppendLine("完成后只汇总每个 taskId 的成功/失败和最终 targetPath；不要报告 Codex 默认目录中的临时文件为正式产物。");
        return prompt.ToString();
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
