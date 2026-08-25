using GAssetForge.App.Services;
using GAssetForge.McpServer;
using System.IO;

namespace GAssetForge.SmokeTests;

internal static class CodexManualFallbackPromptSmokeTests
{
    public static void Run()
    {
        const string tokenVariableName = "GASSETFORGE_MCP_BEARER_TOKEN";
        const string secretTokenValue = "smoke-secret-token-value-must-never-appear";
        var originalTokenValue = Environment.GetEnvironmentVariable(tokenVariableName);
        var projectDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GAssetForge Manual Fallback"));
        var endpoint = new Uri("http://127.0.0.1:43120/mcp", UriKind.Absolute);
        const string batchId = "batch-manual-smoke";
        string[] taskIds = ["task-b", "task-a", " task-c ", "task-b"];

        Environment.SetEnvironmentVariable(tokenVariableName, secretTokenValue);
        try
        {
            var connectionPrompt = CodexManualFallbackPromptBuilder.BuildConnectionPrompt(
                projectDirectory,
                endpoint,
                tokenVariableName);
            var batchPrompt = CodexManualFallbackPromptBuilder.BuildBatchPrompt(
                projectDirectory,
                endpoint,
                tokenVariableName,
                batchId,
                taskIds);

            Contains(connectionPrompt, projectDirectory, "人工连接提示必须包含项目工作目录");
            Contains(connectionPrompt, GenerationAgentPromptBuilder.McpServerName,
                "人工连接提示必须包含 MCP Server 名称");
            Contains(connectionPrompt, endpoint.AbsoluteUri, "人工连接提示必须包含 MCP Endpoint");
            Contains(connectionPrompt, tokenVariableName, "人工连接提示必须包含 Bearer Token 环境变量名");
            Contains(connectionPrompt, "[mcp_servers.gasset_forge]",
                "人工连接提示必须提供 Codex MCP 配置表");
            Contains(connectionPrompt, "bearer_token_env_var", "人工连接提示必须通过环境变量引用令牌");
            Contains(connectionPrompt, "required = true", "人工连接提示必须把本地 MCP 标记为必需");
            Contains(
                connectionPrompt,
                $"tool_timeout_sec = {GenerationMcpToolPolicy.ToolTimeoutSeconds}",
                "人工连接提示必须覆盖 Codex 默认 MCP 工具超时");
            Contains(connectionPrompt, "enabled_tools =", "人工连接提示必须收窄 MCP 工具范围");
            foreach (var toolName in GenerationMcpToolPolicy.EnabledTools)
            {
                Contains(connectionPrompt, toolName, $"人工连接提示必须允许 {toolName}");
            }
            NotContains(connectionPrompt, "create_generation_task",
                "人工连接提示不得开放创建新任务工具");
            Contains(
                connectionPrompt,
                $"default_tools_approval_mode = \"{GenerationMcpToolPolicy.DefaultToolsApprovalMode}\"",
                "人工连接提示必须显式配置受管工具审批模式");
            Contains(connectionPrompt, "server_info", "人工连接提示必须要求真实只读工具探测");
            Contains(connectionPrompt, "GAssetForge.McpServer.exe",
                "人工连接提示必须提供无需动态 MCP 注册的正式本地桥接");
            Contains(connectionPrompt, DirectToolInvocationExecutor.InvokeToolOption,
                "人工连接提示必须调用受限本地桥接入口");
            Contains(connectionPrompt, DirectToolInvocationExecutor.ArgumentsBase64Option,
                "人工连接提示必须安全传递结构化工具参数");
            Contains(connectionPrompt, "两条路径只需有一条成功",
                "人工连接提示必须允许既有 Host 在 MCP 未加载时改走本地桥接");
            NotContains(connectionPrompt, secretTokenValue, "人工连接提示不得泄露 Bearer Token 明文");

            Contains(batchPrompt, projectDirectory, "当前批次提示必须自包含项目工作目录");
            Contains(batchPrompt, endpoint.AbsoluteUri, "当前批次提示必须自包含 MCP Endpoint");
            Contains(batchPrompt, tokenVariableName, "当前批次提示必须自包含令牌环境变量名");
            Contains(batchPrompt, batchId, "当前批次提示必须包含 batchId");
            Contains(batchPrompt, "Invoke-GAssetForgeTool",
                "当前批次提示必须可在既有 Codex Host 中真实认领和回写任务");
            Contains(batchPrompt, "不要再因缺少 Bearer 环境变量停止",
                "当前批次提示必须修复旧人工接力的错误停止条件");
            NotContains(batchPrompt, secretTokenValue, "当前批次提示不得泄露 Bearer Token 明文");
            Contains(batchPrompt, "一张图片一个任务",
                "人工接力必须使用单图任务合同");
            Contains(batchPrompt, "submit_generation_output",
                "人工接力必须把模型源文件交给客户端提交工具");
            Contains(batchPrompt, "sourcePath=<模型返回的绝对路径>",
                "人工接力必须直接提交真实图像模型返回的本地文件");
            Contains(batchPrompt, "recommendedSourcePath",
                "人工接力必须使用 claim 返回的项目内推荐源路径");
            Contains(batchPrompt, "savedPath 为空是协议允许的未落盘状态",
                "人工接力不得把无本地路径的内置可视结果当成可交付产物");
            Contains(batchPrompt, "严禁扫描、按时间挑选或复用 `~/.codex/generated_images`",
                "人工接力不得从 Codex 历史图片目录猜测本次产物");
            Contains(batchPrompt, "run_image_delivery_fallback(taskId, agentId)",
                "人工接力必须使用 GAssetForge Harness 的官方 ImageGen CLI 落盘兜底");
            Contains(batchPrompt, "Agent 禁止自行运行 python、py、uv 或脚本",
                "人工接力不得自行拼装 CLI 或依赖旧 PATH");
            Contains(batchPrompt, "GAssetForge 会自行把原图导入项目运行工作区",
                "人工接力必须把项目落盘和确定性后处理交给客户端");
            Contains(batchPrompt, "Agent 不得自行建目录、搬运、缩放、抠图",
                "人工接力必须禁止 Agent 重复执行客户端职责");
            NotContains(batchPrompt, "output.maxParallelism",
                "单图任务合同不得继续暴露页面并行数");
            NotContains(batchPrompt, "/root/tile_slot_",
                "人工接力不得依赖固定子 Agent 路径");
            NotContains(batchPrompt, "再用受控并行生成这些目标",
                "人工接力不得保留旧批次并行协议");
            NotContains(batchPrompt, "预先绑定 delivery.targetPaths",
                "Agent 不得继续负责预分配目标路径搬运");
            NotContains(batchPrompt, "将每张纯色底模型原图写入同索引 delivery.stagingPaths",
                "Agent 不得继续负责创建和写入项目 staging");
            NotContains(batchPrompt, "逐张调用 process_generation_output",
                "Agent 不得继续承担旧逐图后处理职责");
            NotContains(batchPrompt, "complete_generation_task（",
                "Agent 不得继续承担旧完成登记职责");

            var taskB = IndexOfSingle(batchPrompt, "- task-b", "重复 taskId 必须只保留第一次出现");
            var taskA = IndexOfSingle(batchPrompt, "- task-a", "task-a 必须准确出现一次");
            var taskC = IndexOfSingle(batchPrompt, "- task-c", "taskId 必须去除首尾空白");
            True(taskB < taskA && taskA < taskC, "taskIds 必须保持批次原始稳定顺序");

            var handoffIndex = RequiredIndex(
                batchPrompt,
                "连接检查通过后，人工接管以下已发布批次：",
                "批次提示缺少人工接管段落");
            var executionSegment = batchPrompt[handoffIndex..];
            var claimIndex = RequiredIndex(
                executionSegment,
                "1. 使用 claim_generation_task",
                "批次提示缺少认领步骤");
            var imagegenIndex = RequiredIndex(
                executionSegment,
                "3. 按 preferredSkill 调用真实图像生成能力",
                "批次提示缺少真实图像生成步骤");
            var submitIndex = RequiredIndex(
                executionSegment,
                "4. 内置 ImageGen 返回明确本地路径时",
                "批次提示缺少单图源文件提交步骤");
            var failIndex = RequiredIndex(
                executionSegment,
                "5. 只有内置 imagegen、run_image_delivery_fallback 或 submit_generation_output 返回真实错误时才调用 fail_generation_task",
                "批次提示缺少失败回报步骤");
            True(
                claimIndex < imagegenIndex &&
                imagegenIndex < submitIndex &&
                submitIndex < failIndex,
                "人工批次执行链必须保持 claim → 真实 imagegen → submit → fail 顺序");

            Throws<ArgumentException>(
                () => CodexManualFallbackPromptBuilder.BuildBatchPrompt(
                    projectDirectory,
                    endpoint,
                    tokenVariableName,
                    batchId,
                    []),
                "没有当前任务时不得构造可误发布的批次提示");
            Throws<ArgumentException>(
                () => CodexManualFallbackPromptBuilder.BuildConnectionPrompt(
                    projectDirectory,
                    endpoint,
                    "BAD=TOKEN"),
                "无效令牌环境变量名必须被拒绝");
            Throws<ArgumentException>(
                () => CodexManualFallbackPromptBuilder.BuildConnectionPrompt(
                    projectDirectory,
                    new Uri("https://example.com/mcp", UriKind.Absolute),
                    tokenVariableName),
                "人工兜底不得把临时 Bearer Token 引导到非 loopback Endpoint");
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariableName, originalTokenValue);
        }
    }

    private static int RequiredIndex(string value, string expected, string message)
    {
        var index = value.IndexOf(expected, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException(message);
        }

        return index;
    }

    private static int IndexOfSingle(string value, string expected, string message)
    {
        var firstIndex = RequiredIndex(value, expected, message);
        if (value.IndexOf(expected, firstIndex + expected.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(message);
        }

        return firstIndex;
    }

    private static void Contains(string value, string expected, string message) =>
        True(value.Contains(expected, StringComparison.Ordinal), message);

    private static void NotContains(string value, string unexpected, string message) =>
        True(!value.Contains(unexpected, StringComparison.Ordinal), message);

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
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
}
