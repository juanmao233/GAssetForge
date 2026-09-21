using System.Text;
using GAssetForge.Core;
using GAssetForge.Core.Generation;

namespace GAssetForge.App.Services;

public static class GenerationAgentPromptBuilder
{
    public const string McpServerName = ProductIdentity.McpServerName;
    internal const string GenerationBatchPromptHeader =
        "执行 GAssetForge 已发布的游戏美术素材生成批次。";

    public static string Build(IReadOnlyCollection<string> taskIds) =>
        BuildCore(taskIds, null, null);

    internal static string Build(
        IReadOnlyCollection<string> taskIds,
        ImageDeliveryPreflightResult preflight) => BuildCore(taskIds, preflight, null);

    internal static string Build(
        IReadOnlyCollection<string> taskIds,
        IReadOnlyDictionary<string, string> claimIdentities) =>
        BuildCore(taskIds, null, claimIdentities);

    internal static string Build(
        IReadOnlyCollection<string> taskIds,
        ImageDeliveryPreflightResult preflight,
        IReadOnlyDictionary<string, string> claimIdentities) =>
        BuildCore(taskIds, preflight, claimIdentities);

    private static string BuildCore(
        IReadOnlyCollection<string> taskIds,
        ImageDeliveryPreflightResult? preflight,
        IReadOnlyDictionary<string, string>? claimIdentities)
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

        if (claimIdentities is not null)
        {
            foreach (var taskId in normalizedIds)
            {
                if (!claimIdentities.TryGetValue(taskId, out var agentId))
                {
                    throw new ArgumentException(
                        $"任务 {taskId} 缺少受管 agentId 映射。",
                        nameof(claimIdentities));
                }

                GenerationInputValidator.ValidateAgentId(agentId);
            }
        }

        var prompt = new StringBuilder();
        prompt.AppendLine(GenerationBatchPromptHeader);
        prompt.AppendLine($"默认只使用名为 `{McpServerName}` 的 MCP Server 读取并更新这些任务；只有提示明确提供 GAssetForge 官方本地任务桥接 CLI 时，才允许通过该桥接调用同名工具：");
        foreach (var taskId in normalizedIds)
        {
            prompt.Append("- ").AppendLine(taskId);
        }

        if (claimIdentities is not null)
        {
            prompt.AppendLine("每个任务的 agentId 已由 GAssetForge 冻结如下；从 claim 到 submit/fallback/fail 的全部工具调用都必须逐字使用对应值：");
            foreach (var taskId in normalizedIds)
            {
                prompt.Append("  ")
                    .Append(taskId)
                    .Append(" => `")
                    .Append(claimIdentities[taskId])
                    .AppendLine("`");
            }

            prompt.AppendLine("Pending 任务使用软件派生的稳定 Codex 身份；已由 GAssetForge 受管身份认领的任务复用其精确冻结 ClaimedBy。相同身份重复 claim 是断线后的幂等续作，应继续使用原 Run 和冻结路径。若工具返回任何不同所有者，立即停止该项并报告所有权冲突，不得冒用、抢占或改写所有者。");
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
        prompt.AppendLine("1. 使用 claim_generation_task(taskId, agentId) 读取冻结的 prompt、风格参考、背景色合同、delivery.workspace，以及软件计算好的项目内绝对 recommendedSourcePath。若上方给出了逐任务 agentId 映射，必须使用该任务对应的精确值；同一身份重复 claim 是续作，不要另建 generation task。");
        prompt.AppendLine("2. 先检查 claim 返回的 imageGeneration.regionEdit。若 operationMode=region-edit，必须立即调用 run_image_delivery_fallback(taskId, agentId)，由 Harness 使用唯一 authoritative base 和冻结 mask 执行 Images Edit API；当前默认 gpt-image-2 对图片输入固有使用高保真模式，因此不得额外传入其不支持的 input-fidelity 参数。提交阶段由软件逐像素保留 mask 外内容。region-edit 禁止先调用普通整图 image_gen/ImageGen，禁止把 style.referenceSourcePaths 中的历史图片作为编辑输入，也禁止绕过 fallback 直接提交自行生成的整图。若声明了 regionEdit 但字段损坏或工具拒绝，立即按不可重试合同错误报告，绝不能降级为普通生成。");
        prompt.AppendLine("3. 仅对非 region-edit 任务执行本步。若任务已有有效最终 target，submit_generation_output 会幂等返回。若精确 recommendedSourcePath 已存在且是可读的有效 PNG，说明本任务可能在模型落盘后断连；必须先直接以该精确路径调用 submit_generation_output，成功后不得再次生成。软件预检会把发现的损坏文件安全隔离；若 claim 后该精确路径又出现无效文件，停止该项并报告明确诊断，禁止覆盖或使用 --force。无需用 PowerShell 或其它命令扫描目录。");
        prompt.AppendLine("4. 仅对非 region-edit 任务按 preferredSkill 调用真实图像生成能力。preferredSkill=imagegen 时立即使用当前 Codex Host 的内置 image_gen/ImageGen。仅当本次调用返回明确的新绝对本地 PNG savedPath/sourcePath，且该文件存在、可读时，才以该路径调用 submit_generation_output。只出现可视图片但 savedPath 为空是协议允许的未落盘状态，不算取得可提交产物；此时必须立即调用 run_image_delivery_fallback(taskId, agentId)。该窄工具会由 GAssetForge Harness 从冻结任务读取完整提示、required rules、风格参考、画幅、纯色底和 recommendedSourcePath，自行发现 Python 与官方 image_gen.py、执行 Image API CLI、验证精确 PNG、完成本地后处理并自动提交任务。Agent 禁止自行运行 python、py、uv 或脚本，严禁扫描、按时间挑选或复用 `~/.codex/generated_images` 中的历史 PNG，也不得用 PowerShell、System.Drawing、Pillow、SVG、Canvas 或程序化图形代替模型生成。");
        prompt.AppendLine("5. 非 region-edit 的内置 ImageGen 返回明确本地路径时，只有确认该 sourcePath 文件存在、可读且具有有效 PNG 签名后，才调用 submit_generation_output(taskId, agentId, outputIndex=0, sourcePath=<模型返回的绝对路径>)。GAssetForge 会自行把原图导入项目运行工作区并归档，并完成尺寸/画布归一化、补齐首道底色、背景剔除、约束校验、最终原子提交和任务完成登记。若 submit 或 fallback 正常返回 outcome=retryable_validation_failure，表示图片及中间产物已经真实落盘，但几何质量或局部编辑选区外差异未通过；这不是 Runtime 或图片交付错误。不要再调用 fail_generation_task，也不要包装成 image_delivery_fallback_failed；记录 code/subCode、尺寸和诊断后继续处理批次中的其它 task，桌面端会按冻结重试策略创建新任务。Agent 不得自行建目录、搬运、缩放、抠图、修改 Alpha 或调用 process_generation_output/complete_generation_task 重复施工；只允许检查本次内置调用明确返回的 sourcePath 或 claim 返回的精确 recommendedSourcePath，不得遍历其它图片目录。");
        prompt.AppendLine("6. 只有内置 imagegen、run_image_delivery_fallback 或 submit_generation_output 真正以 MCP tool error 失败时才调用 fail_generation_task，并记录工具返回的准确错误；不得把 Claimed 当成 Completed。内置工具仅有可视结果且 savedPath 为空时不得直接 fail，必须先调用 Harness 兜底工具。只有 Python/image_gen.py/Image API 本地交付阶段失败才使用 failureCode=image_delivery_fallback_failed；输出几何校验使用 failureCode=output_geometry_validation_failed，局部编辑越界校验使用 failureCode=output_region_edit_validation_failed，并保留具体 failureSubCode。认证、配额、路径权限、冻结合同损坏等需要人工介入的问题必须 retryable=false；明确的几何/局部编辑质量问题或暂时性网络错误才可 retryable=true。");
        prompt.AppendLine("兼容说明：若 claim 返回的旧任务没有 delivery.workspace，才按旧合同使用 delivery.stagingPaths、process_generation_output 和 complete_generation_task；不要把旧流程套到新任务。");
        prompt.AppendLine("完成后只汇总每个 taskId 的成功/失败和最终 targetPath；不要报告 Codex 默认目录中的临时文件为正式产物。");
        return prompt.ToString();
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
