using System.Text;
using GAssetForge.Core;

namespace GAssetForge.App.Services;

public static class GenerationAgentPromptBuilder
{
    public const string McpServerName = ProductIdentity.McpServerName;

    public static string Build(IReadOnlyCollection<string> taskIds)
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
        prompt.AppendLine("对每个任务只执行不可替代的生成步骤；单项失败不得阻止其余任务：");
        prompt.AppendLine("1. 使用 claim_generation_task(taskId, agentId) 读取冻结的 prompt、风格参考、背景色合同和 delivery.workspace。不要另建 generation task。");
        prompt.AppendLine("2. 若任务已有有效最终 target，submit_generation_output 会幂等返回；无需用 PowerShell 或其它命令扫描目录。");
        prompt.AppendLine("3. 按 preferredSkill 调用真实图像生成能力。preferredSkill=imagegen 时立即使用当前 Codex Host 的内置 image_gen/ImageGen；只有内置能力确实未暴露时才使用官方 image_gen.py/API。不得用 PowerShell、System.Drawing、Pillow、SVG、Canvas 或程序化图形代替模型生成。生成提示必须完整保留 claim 返回的 constraints.prompt 和 required perspective rules，尤其是 axisA/axisB、两条边的绝对屏幕角度及禁止额外旋转条款；不得把控制角 90°、旋转 0°改写成 diamond/rhombus、on-point square 或角点朝上的 45° 方形。生成提示还必须明确使用 output.backgroundRemoval 指定的纯色底。");
        prompt.AppendLine("4. 图像模型返回本地文件后，直接调用 submit_generation_output(taskId, agentId, outputIndex=0, sourcePath=<模型返回的绝对路径>)。允许 sourcePath 位于 Codex 默认 generated_images；GAssetForge 会自行把原图导入项目运行工作区，并完成尺寸/画布归一化、补齐首道底色、背景剔除、约束校验、最终原子提交和任务完成登记。Agent 不得自行建目录、搬运、缩放、抠图、修改 Alpha 或调用 process_generation_output/complete_generation_task 重复施工。");
        prompt.AppendLine("5. 只有 imagegen 或 submit_generation_output 真实失败时才调用 fail_generation_task，记录工具返回的准确错误；不得把 Claimed 当成 Completed。");
        prompt.AppendLine("兼容说明：若 claim 返回的旧任务没有 delivery.workspace，才按旧合同使用 delivery.stagingPaths、process_generation_output 和 complete_generation_task；不要把旧流程套到新任务。");
        prompt.AppendLine("完成后只汇总每个 taskId 的成功/失败和最终 targetPath；不要报告 Codex 默认目录中的临时文件为正式产物。");
        return prompt.ToString();
    }
}
