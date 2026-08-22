using System.IO;
using System.Text;
using System.Text.Json;

namespace GAssetForge.App.Services;

/// <summary>
/// Builds paste-ready Codex instructions for manually recovering task access and claiming a batch
/// when the managed Agent turn cannot do so. Existing Codex hosts cannot dynamically load MCP or
/// inherit a token created by another process, so the prompt also exposes the official restricted
/// one-shot task bridge. The builder never reads or prints the bearer token value.
/// </summary>
public static class CodexManualFallbackPromptBuilder
{
    public static string BuildConnectionPrompt(
        string projectDirectory,
        Uri mcpEndpoint,
        string tokenEnvironmentVariableName)
    {
        var context = ValidateContext(projectDirectory, mcpEndpoint, tokenEnvironmentVariableName);
        var prompt = new StringBuilder();
        prompt.AppendLine("请在当前 Codex 会话中恢复 GAssetForge 本地任务访问。");
        prompt.AppendLine();
        AppendConnectionContext(prompt, context);
        prompt.AppendLine();
        prompt.AppendLine("Codex MCP 等价配置（用于核对当前 Host 配置或下一次启动；聊天文字本身不能动态加载未注册的 MCP）：");
        prompt.AppendLine("```toml");
        prompt.Append('[').Append("mcp_servers.").Append(GenerationAgentPromptBuilder.McpServerName).AppendLine("]");
        prompt.Append("url = ").AppendLine(TomlString(context.Endpoint.AbsoluteUri));
        prompt.Append("bearer_token_env_var = ").AppendLine(TomlString(context.TokenEnvironmentVariableName));
        prompt.AppendLine("required = true");
        prompt.Append("enabled_tools = ").AppendLine(GenerationMcpToolPolicy.BuildTomlToolArray());
        prompt.Append("default_tools_approval_mode = ")
            .AppendLine(TomlString(GenerationMcpToolPolicy.DefaultToolsApprovalMode));
        prompt.AppendLine("```");
        prompt.AppendLine();
        prompt.AppendLine("已经启动的 Codex Host 无法仅靠聊天文字动态加载上述 MCP，也无法继承由 GAssetForge 进程稍后创建的临时 Bearer 环境变量。若当前 Host 没有 `gasset_forge`，请使用下面的官方本地任务桥接；它复用同一任务状态机和产物校验，只开放上述受管工具，不需要也不会输出 Bearer Token：");
        prompt.AppendLine("```powershell");
        prompt.Append("$GAssetForgeBridge = ")
            .AppendLine(PowerShellSingleQuoted(context.BridgeExecutablePath));
        prompt.AppendLine("function Invoke-GAssetForgeTool([string]$ToolName, [hashtable]$ToolArguments) {");
        prompt.AppendLine("    $Json = $ToolArguments | ConvertTo-Json -Depth 64 -Compress");
        prompt.AppendLine("    $Encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Json))");
        prompt.AppendLine("    $Output = & $GAssetForgeBridge '--invoke-tool' $ToolName '--arguments-base64' $Encoded");
        prompt.AppendLine("    if ($LASTEXITCODE -ne 0) { throw (($Output | Out-String).Trim()) }");
        prompt.AppendLine("    ($Output | Out-String) | ConvertFrom-Json");
        prompt.AppendLine("}");
        prompt.AppendLine("Invoke-GAssetForgeTool 'server_info' @{}");
        prompt.AppendLine("```");
        prompt.AppendLine();
        prompt.AppendLine("连接检查必须按以下规则执行：");
        prompt.Append("1. 确认当前工作目录为上面的项目目录；不要切换到其它项目。")
            .AppendLine();
        prompt.Append("2. 检查当前 Codex Host 的 MCP 列表，确认 `")
            .Append(GenerationAgentPromptBuilder.McpServerName)
            .AppendLine("` 是否已加载并处于 ready；不要把仅看到配置文本当成已连接。");
        prompt.Append("3. 如果 MCP 工具真实可用，通过 `")
            .Append(GenerationAgentPromptBuilder.McpServerName)
            .AppendLine("` 调用只读 `server_info`；如果 MCP 不存在，则运行上面的本地桥接 `server_info`。两条路径只需有一条成功。");
        prompt.AppendLine("4. 核对 server_info 返回的 dataRoot、generationStore 和 Assets 根；本地桥接成功不等于 MCP 已动态加载，汇报时必须准确说明实际采用的路径。");
        prompt.AppendLine("5. 只有 MCP 与本地桥接都失败时才停止；不得因为当前 Host 缺少 Bearer 环境变量而跳过可用的本地桥接，也不得猜测或索要令牌值。");
        prompt.AppendLine("6. 不要在聊天、日志、项目文件或 config.toml 中打印/写入 Bearer Token 明文；配置中只允许引用上述环境变量名。");
        return prompt.ToString();
    }

    public static string BuildBatchPrompt(
        string projectDirectory,
        Uri mcpEndpoint,
        string tokenEnvironmentVariableName,
        string batchId,
        IReadOnlyCollection<string> taskIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(taskIds);

        var normalizedBatchId = RequireSingleLineValue(batchId.Trim(), nameof(batchId));
        var executionPrompt = GenerationAgentPromptBuilder.Build(taskIds);
        var prompt = new StringBuilder();
        prompt.Append(BuildConnectionPrompt(projectDirectory, mcpEndpoint, tokenEnvironmentVariableName).TrimEnd());
        prompt.AppendLine();
        prompt.AppendLine();
        prompt.AppendLine("连接检查通过后，人工接管以下已发布批次：");
        prompt.Append("- batchId：").AppendLine(normalizedBatchId);
        prompt.AppendLine("- 只处理下方按固定顺序列出的 taskIds；不得领取其它待处理任务，也不得创建新的 generation task。");
        prompt.AppendLine("- 必须发生真实 MCP 工具调用，或使用上文 `Invoke-GAssetForgeTool` 对同名工具执行真实本地桥接调用；仅回复“已收到/将执行”不算认领或完成。");
        prompt.AppendLine();
        prompt.Append(executionPrompt.TrimEnd());
        prompt.AppendLine();
        prompt.AppendLine();
        prompt.AppendLine("如果当前 Host 没有 `gasset_forge` 工具，请将上面执行链中出现的每个工具调用改为 `Invoke-GAssetForgeTool '<同名工具>' @{...}`；新任务仍严格保持 claim_generation_task → 真实 imagegen → submit_generation_output，只有真实失败才 fail_generation_task。不要再因缺少 Bearer 环境变量停止。");
        prompt.AppendLine("项目工作区、分类目录、原图导入、尺寸与底色归一化、背景剔除、最终校验和任务完成登记均由 GAssetForge 执行；人工接力不得再自行扫描、搬运或改写中间图片。");
        prompt.AppendLine("结束前再次查询这些 taskIds 的状态，逐项汇报 Completed/Failed 与实际产物路径；任何仍为 Pending/Claimed 的任务都必须明确列出，不能把当前批次报告为完成。");
        return prompt.ToString();
    }

    private static ConnectionContext ValidateContext(
        string projectDirectory,
        Uri mcpEndpoint,
        string tokenEnvironmentVariableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(mcpEndpoint);
        ValidateTokenEnvironmentVariableName(tokenEnvironmentVariableName);

        var normalizedProjectDirectory = Path.GetFullPath(projectDirectory.Trim());
        if (!mcpEndpoint.IsAbsoluteUri ||
            (!string.Equals(mcpEndpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(mcpEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("MCP Endpoint 必须是绝对 HTTP 或 HTTPS 地址。", nameof(mcpEndpoint));
        }

        if (!mcpEndpoint.IsLoopback)
        {
            throw new ArgumentException("人工兜底只允许连接 loopback MCP Endpoint。", nameof(mcpEndpoint));
        }

        if (!string.IsNullOrEmpty(mcpEndpoint.UserInfo))
        {
            throw new ArgumentException("MCP Endpoint 不得内嵌认证信息。", nameof(mcpEndpoint));
        }

        return new ConnectionContext(
            normalizedProjectDirectory,
            mcpEndpoint,
            tokenEnvironmentVariableName.Trim(),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "GAssetForge.McpServer.exe")));
    }

    private static void AppendConnectionContext(StringBuilder prompt, ConnectionContext context)
    {
        prompt.AppendLine("连接上下文：");
        prompt.Append("- 项目工作目录：").AppendLine(context.ProjectDirectory);
        prompt.Append("- MCP Server 名称：").AppendLine(GenerationAgentPromptBuilder.McpServerName);
        prompt.Append("- MCP Endpoint：").AppendLine(context.Endpoint.AbsoluteUri);
        prompt.Append("- Bearer Token 环境变量名：").AppendLine(context.TokenEnvironmentVariableName);
        prompt.AppendLine("- Bearer Token 值：只允许由 Codex 进程从该环境变量读取，禁止回显或持久化。");
        prompt.Append("- 官方本地任务桥接：").AppendLine(context.BridgeExecutablePath);
    }

    private static void ValidateTokenEnvironmentVariableName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        if (!IsAsciiLetter(trimmed[0]) && trimmed[0] != '_')
        {
            throw new ArgumentException("Bearer Token 环境变量名必须以英文字母或下划线开头。", nameof(name));
        }

        for (var index = 1; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (!IsAsciiLetter(character) && !char.IsAsciiDigit(character) && character != '_')
            {
                throw new ArgumentException("Bearer Token 环境变量名只能包含英文字母、数字和下划线。", nameof(name));
            }
        }
    }

    private static string RequireSingleLineValue(string value, string parameterName)
    {
        if (value.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ArgumentException("值必须位于单行内。", parameterName);
        }

        return value;
    }

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static string TomlString(string value) => JsonSerializer.Serialize(value);

    private static string PowerShellSingleQuoted(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private sealed record ConnectionContext(
        string ProjectDirectory,
        Uri Endpoint,
        string TokenEnvironmentVariableName,
        string BridgeExecutablePath);
}
