using System.Text.Json;

namespace GAssetForge.McpServer;

internal static class JsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

internal sealed class JsonRpcException(int code, string message, object? data = null) : Exception(message)
{
    public int Code { get; } = code;

    public object? ErrorData { get; } = data;
}

internal static class JsonArguments
{
    public static string RequiredString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"参数 {name} 必须是非空字符串。");
        }

        return value.GetString()!.Trim();
    }

    public static string? OptionalString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"参数 {name} 必须是字符串。");
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public static JsonElement OptionalObject(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
        {
            return default;
        }

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"参数 {name} 必须是对象。");
        }

        return value;
    }

    public static IReadOnlyList<string> RequiredStringArray(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"参数 {name} 必须是字符串数组。");
        }

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"参数 {name} 只能包含非空字符串。");
            }

            items.Add(item.GetString()!.Trim());
        }

        if (items.Count == 0)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"参数 {name} 至少需要一个路径。");
        }

        return items;
    }
}
