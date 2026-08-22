using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GAssetForge.App.Services.AgentRuntime;

internal sealed record ParsedAgentActivity(
    DateTimeOffset Timestamp,
    AgentActivityCategory Category,
    string Message,
    bool IsWarning,
    string? ThreadId,
    string? TurnId,
    string? ItemId,
    string? ItemType,
    string? TaskId,
    string EventName,
    bool IsTerminal);

/// <summary>
/// Normalizes Codex app-server notifications and codex exec JSONL events into a small,
/// display-safe activity surface. Raw reasoning and command output are intentionally omitted.
/// </summary>
internal sealed class AgentActivityParser
{
    internal const int MaximumMessageLength = 2_000;
    internal const int MaximumIdentifierLength = 256;

    private static readonly Regex OpenAiSecretPattern = new(
        @"\bsk-[A-Za-z0-9_-]{8,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerSecretPattern = new(
        @"\b(Bearer)\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex NamedSecretPattern = new(
        @"\b((?:[a-z0-9]+[_-])*(?:api[_-]?key|access[_-]?token|authorization|password|secret))\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly Dictionary<string, StringBuilder> _agentMessageBuffers =
        new(StringComparer.Ordinal);

    internal ParsedAgentActivity? Parse(
        AgentNotificationEventArgs notification,
        DateTimeOffset timestamp,
        string? fallbackThreadId = null,
        string? fallbackTurnId = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var sourceEventName = CleanIdentifier(notification.Method, 128) ?? string.Empty;
        if (sourceEventName.Length == 0)
        {
            return null;
        }

        var eventName = NormalizeEventName(sourceEventName);
        var eventKey = CollapseToken(eventName);
        var payload = notification.Parameters;
        var threadId = ReadFirstString(payload, "threadId", "thread_id") ??
                       CleanIdentifier(fallbackThreadId, MaximumIdentifierLength);
        var turnId = ReadFirstString(payload, "turnId", "turn_id") ??
                     CleanIdentifier(fallbackTurnId, MaximumIdentifierLength);

        if (eventKey is "turnstarted" or "turncompleted" or "turnfailed")
        {
            return ParseTurn(
                payload,
                timestamp,
                eventName,
                eventKey,
                threadId,
                turnId);
        }

        if (eventKey == "itemagentmessagedelta")
        {
            return ParseAgentMessageDelta(payload, timestamp, eventName, threadId, turnId);
        }

        if (eventKey is "itemstarted" or "itemcompleted")
        {
            return ParseItem(
                payload,
                timestamp,
                eventName,
                eventKey == "itemcompleted",
                threadId,
                turnId);
        }

        if (eventKey is "warning" or "configwarning")
        {
            var warning = ReadFirstDisplayString(payload, "message", "summary", "details") ??
                          "Agent Runtime 返回了一条警告。";
            return Create(
                timestamp,
                AgentActivityCategory.Warning,
                warning,
                isWarning: true,
                threadId,
                turnId,
                eventName: eventName);
        }

        if (eventKey == "error")
        {
            return Create(
                timestamp,
                AgentActivityCategory.Error,
                ReadErrorMessage(payload) ?? "Agent Runtime 返回了未说明原因的错误。",
                isWarning: true,
                threadId,
                turnId,
                eventName: eventName,
                isTerminal: false);
        }

        return null;
    }

    internal void Reset() => _agentMessageBuffers.Clear();

    private ParsedAgentActivity ParseTurn(
        JsonElement payload,
        DateTimeOffset timestamp,
        string eventName,
        string eventKey,
        string? threadId,
        string? fallbackTurnId)
    {
        var turn = TryReadObject(payload, "turn", out var turnElement)
            ? turnElement
            : payload;
        var turnId = ReadFirstString(turn, "id", "turnId", "turn_id") ?? fallbackTurnId;
        var status = ReadFirstString(turn, "status") ??
                     (eventKey == "turnfailed" ? "failed" : null);
        var failed = eventKey == "turnfailed" ||
                     string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
        var interrupted = string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase);
        var completed = eventKey is "turncompleted" or "turnfailed";
        var message = completed
            ? failed
                ? $"Agent Turn {ShortId(turnId)} 执行失败：{ReadErrorMessage(turn) ?? ReadErrorMessage(payload) ?? "未提供错误详情"}"
                : interrupted
                    ? $"Agent Turn {ShortId(turnId)} 已中断。"
                    : $"Agent Turn {ShortId(turnId)} 已结束（{status ?? "completed"}）。"
            : $"Agent Turn {ShortId(turnId)} 已开始。";

        if (completed && !string.IsNullOrWhiteSpace(turnId))
        {
            RemoveTurnBuffers(turnId);
        }

        return Create(
            timestamp,
            completed ? AgentActivityCategory.TurnCompleted : AgentActivityCategory.TurnStarted,
            message,
            failed || interrupted,
            threadId,
            turnId,
            eventName: eventName,
            isTerminal: completed);
    }

    private ParsedAgentActivity? ParseAgentMessageDelta(
        JsonElement payload,
        DateTimeOffset timestamp,
        string eventName,
        string? threadId,
        string? turnId)
    {
        var delta = ReadFirstDisplayString(payload, "delta", "text");
        if (string.IsNullOrEmpty(delta))
        {
            return null;
        }

        var itemId = ReadFirstString(payload, "itemId", "item_id");
        var key = BuildMessageBufferKey(turnId, itemId);
        var message = AppendAgentMessage(key, delta);
        return Create(
            timestamp,
            AgentActivityCategory.AgentMessage,
            message,
            isWarning: false,
            threadId,
            turnId,
            itemId,
            "agentMessage",
            eventName: eventName);
    }

    private ParsedAgentActivity ParseItem(
        JsonElement payload,
        DateTimeOffset timestamp,
        string eventName,
        bool completed,
        string? threadId,
        string? turnId)
    {
        var item = TryReadObject(payload, "item", out var itemElement)
            ? itemElement
            : payload;
        threadId ??= ReadFirstString(item, "threadId", "thread_id");
        turnId ??= ReadFirstString(item, "turnId", "turn_id");
        var itemId = ReadFirstString(item, "id", "itemId", "item_id");
        var rawItemType = ReadFirstString(item, "type") ?? "unknown";
        var itemType = CanonicalItemType(rawItemType);
        var status = ReadFirstString(item, "status");
        var itemFailed = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                         ReadErrorMessage(item) is not null;
        var taskId = ReadTaskId(item);

        var category = ResolveItemCategory(itemType, completed);
        string message;
        switch (CollapseToken(itemType))
        {
            case "agentmessage":
                var authoritativeText = ReadFirstDisplayString(item, "text");
                var key = BuildMessageBufferKey(turnId, itemId);
                message = !string.IsNullOrWhiteSpace(authoritativeText)
                    ? LimitDisplayText(authoritativeText)
                    : ReadBufferedMessage(key) ??
                      (completed ? "Agent 已完成一条回复。" : "Agent 正在组织回复。");
                if (completed)
                {
                    _agentMessageBuffers.Remove(key);
                }

                break;
            case "plan":
                message = ReadFirstDisplayString(item, "text") is { Length: > 0 } planText
                    ? LimitDisplayText(planText)
                    : completed
                        ? "Agent 已更新执行计划。"
                        : "Agent 正在制定执行计划。";
                break;
            case "reasoning":
                message = completed ? "Agent 已完成当前分析步骤。" : "Agent 正在分析任务。";
                break;
            case "mcptoolcall":
                message = BuildToolMessage(item, completed, itemFailed, taskId);
                break;
            case "dynamictoolcall":
                message = BuildDynamicToolMessage(item, completed, itemFailed, taskId);
                break;
            case "commandexecution":
                message = itemFailed
                    ? "Agent 命令执行失败。"
                    : completed
                        ? "Agent 命令执行完成。"
                        : "Agent 正在执行命令。";
                break;
            case "imageview":
                message = BuildImageViewMessage(item, completed);
                break;
            case "filechange":
                message = completed ? "Agent 已完成文件变更步骤。" : "Agent 正在处理文件变更。";
                break;
            default:
                message = $"Agent 项目 {LimitIdentifier(itemType, 80)}{(completed ? " 已完成。" : " 已开始。")}";
                break;
        }

        if (itemFailed && ReadErrorMessage(item) is { Length: > 0 } itemError &&
            !message.Contains(itemError, StringComparison.Ordinal))
        {
            message = $"{message.TrimEnd('。')}：{itemError}";
        }

        return Create(
            timestamp,
            itemFailed ? AgentActivityCategory.Error : category,
            message,
            itemFailed,
            threadId,
            turnId,
            itemId,
            itemType,
            taskId,
            eventName,
            completed);
    }

    private static AgentActivityCategory ResolveItemCategory(string itemType, bool completed) =>
        CollapseToken(itemType) switch
        {
            "agentmessage" => AgentActivityCategory.AgentMessage,
            "plan" => AgentActivityCategory.Plan,
            "reasoning" => AgentActivityCategory.Reasoning,
            "mcptoolcall" or "dynamictoolcall" => completed
                ? AgentActivityCategory.ToolCompleted
                : AgentActivityCategory.ToolStarted,
            "commandexecution" => completed
                ? AgentActivityCategory.CommandCompleted
                : AgentActivityCategory.CommandStarted,
            _ => completed ? AgentActivityCategory.ItemCompleted : AgentActivityCategory.ItemStarted
        };

    private static string BuildToolMessage(
        JsonElement item,
        bool completed,
        bool failed,
        string? taskId)
    {
        var server = ReadFirstString(item, "server") ?? "MCP";
        var tool = ReadFirstString(item, "tool") ?? "unknown";
        var state = failed ? "调用失败" : completed ? "调用完成" : "正在调用";
        return $"MCP {LimitIdentifier(server, 80)}.{LimitIdentifier(tool, 120)} {state}{TaskSuffix(taskId)}。";
    }

    private static string BuildDynamicToolMessage(
        JsonElement item,
        bool completed,
        bool failed,
        string? taskId)
    {
        var tool = ReadFirstString(item, "tool") ?? "unknown";
        var state = failed ? "执行失败" : completed ? "执行完成" : "正在执行";
        return $"Agent 工具 {LimitIdentifier(tool, 120)} {state}{TaskSuffix(taskId)}。";
    }

    private static string BuildImageViewMessage(JsonElement item, bool completed)
    {
        var path = ReadFirstString(item, "path");
        string? fileName = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                fileName = Path.GetFileName(path);
            }
            catch
            {
                fileName = null;
            }
        }

        var target = string.IsNullOrWhiteSpace(fileName)
            ? "图片"
            : $"图片 {LimitIdentifier(fileName, 120)}";
        return completed ? $"Agent 已检查{target}。" : $"Agent 正在检查{target}。";
    }

    private static string? ReadTaskId(JsonElement item)
    {
        if (!item.TryGetProperty("arguments", out var arguments))
        {
            return null;
        }

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            return ReadFirstString(arguments, "taskId", "task_id");
        }

        if (arguments.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(arguments.GetString()) ||
            arguments.GetString()!.Length > 32_768)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments.GetString()!);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ReadFirstString(document.RootElement, "taskId", "task_id")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadErrorMessage(JsonElement element)
    {
        if (ReadFirstDisplayString(element, "message") is { Length: > 0 } directMessage)
        {
            return LimitDisplayText(directMessage);
        }

        if (!element.TryGetProperty("error", out var error) ||
            error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (error.ValueKind == JsonValueKind.String)
        {
            return LimitDisplayText(error.GetString() ?? string.Empty);
        }

        return error.ValueKind == JsonValueKind.Object &&
               ReadFirstDisplayString(error, "message") is { Length: > 0 } nestedMessage
            ? LimitDisplayText(nestedMessage)
            : null;
    }

    private string AppendAgentMessage(string key, string delta)
    {
        if (!_agentMessageBuffers.TryGetValue(key, out var buffer))
        {
            buffer = new StringBuilder(Math.Min(MaximumMessageLength, Math.Max(64, delta.Length)));
            _agentMessageBuffers[key] = buffer;
        }

        buffer.Append(FilterDisplayCharacters(delta));
        var current = buffer.ToString();
        var redacted = RedactCommonSecrets(current);
        if (!string.Equals(redacted, current, StringComparison.Ordinal))
        {
            buffer.Clear();
            buffer.Append(redacted);
        }

        if (buffer.Length > MaximumMessageLength)
        {
            var tail = buffer.ToString(buffer.Length - MaximumMessageLength + 1, MaximumMessageLength - 1);
            buffer.Clear();
            buffer.Append('…').Append(tail);
        }

        return LimitDisplayText(buffer.ToString());
    }

    private string? ReadBufferedMessage(string key) =>
        _agentMessageBuffers.TryGetValue(key, out var buffer) && buffer.Length > 0
            ? LimitDisplayText(buffer.ToString())
            : null;

    private void RemoveTurnBuffers(string turnId)
    {
        var prefix = $"{turnId}/";
        foreach (var key in _agentMessageBuffers.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                     .ToArray())
        {
            _agentMessageBuffers.Remove(key);
        }
    }

    private static string BuildMessageBufferKey(string? turnId, string? itemId) =>
        $"{turnId ?? "unknown-turn"}/{itemId ?? "agent-message"}";

    private static string NormalizeEventName(string eventName) => eventName.Replace('.', '/');

    private static string CanonicalItemType(string itemType) => CollapseToken(itemType) switch
    {
        "agentmessage" => "agentMessage",
        "mcptoolcall" => "mcpToolCall",
        "dynamictoolcall" => "dynamicToolCall",
        "commandexecution" => "commandExecution",
        "filechange" => "fileChange",
        "imageview" => "imageView",
        "websearch" => "webSearch",
        "reasoning" => "reasoning",
        "plan" => "plan",
        _ => LimitIdentifier(itemType, 80)
    };

    private static string CollapseToken(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static ParsedAgentActivity Create(
        DateTimeOffset timestamp,
        AgentActivityCategory category,
        string message,
        bool isWarning,
        string? threadId,
        string? turnId,
        string? itemId = null,
        string? itemType = null,
        string? taskId = null,
        string? eventName = null,
        bool isTerminal = false) =>
        new(
            timestamp,
            category,
            LimitDisplayText(message),
            isWarning,
            CleanIdentifier(threadId, MaximumIdentifierLength),
            CleanIdentifier(turnId, MaximumIdentifierLength),
            CleanIdentifier(itemId, MaximumIdentifierLength),
            CleanIdentifier(itemType, 80),
            CleanIdentifier(taskId, MaximumIdentifierLength),
            CleanIdentifier(eventName, 128) ?? string.Empty,
            isTerminal);

    private static bool TryReadObject(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Object)
        {
            value = property;
            return true;
        }

        value = default;
        return false;
    }

    private static string? ReadFirstString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property) ||
                property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            var value = property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    ? property.ToString()
                    : null;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return CleanIdentifier(value, MaximumIdentifierLength);
            }
        }

        return null;
    }

    private static string? ReadFirstDisplayString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property) ||
                property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            var value = property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    ? property.ToString()
                    : null;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return FilterDisplayCharacters(value);
            }
        }

        return null;
    }

    private static string LimitDisplayText(string value)
    {
        var filtered = RedactCommonSecrets(FilterDisplayCharacters(value)).Trim();
        if (filtered.Length <= MaximumMessageLength)
        {
            return filtered;
        }

        return "…" + filtered.Substring(filtered.Length - (MaximumMessageLength - 1));
    }

    private static string FilterDisplayCharacters(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsControl(character) || character is '\r' or '\n' or '\t')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string RedactCommonSecrets(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = OpenAiSecretPattern.Replace(value, "[REDACTED]");
        redacted = BearerSecretPattern.Replace(redacted, "$1 [REDACTED]");
        return NamedSecretPattern.Replace(redacted, "$1=[REDACTED]");
    }

    private static string? CleanIdentifier(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var filtered = FilterDisplayCharacters(value).Trim();
        return filtered.Length <= maximumLength ? filtered : filtered[..maximumLength];
    }

    private static string LimitIdentifier(string? value, int maximumLength) =>
        CleanIdentifier(value, maximumLength) ?? "unknown";

    private static string ShortId(string? value)
    {
        var normalized = CleanIdentifier(value, MaximumIdentifierLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "(待分配)";
        }

        return normalized.Length <= 12 ? normalized : normalized[..12];
    }

    private static string TaskSuffix(string? taskId) => string.IsNullOrWhiteSpace(taskId)
        ? string.Empty
        : $" · task {ShortId(taskId)}";
}
