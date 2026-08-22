using System.Text.Json;
using System.Text.Json.Serialization;

namespace GAssetForge.McpServer;

internal sealed record McpToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema,
    Func<JsonElement, CancellationToken, Task<object?>> Handler);

internal sealed class McpToolCatalog(IEnumerable<McpToolDefinition> tools)
{
    private readonly IReadOnlyList<McpToolDefinition> _tools = tools.ToArray();
    private readonly IReadOnlyDictionary<string, McpToolDefinition> _toolsByName = tools.ToDictionary(
        tool => tool.Name,
        StringComparer.Ordinal);

    public object DescribeTools() => new
    {
        tools = _tools.Select(tool => new
        {
            name = tool.Name,
            description = tool.Description,
            inputSchema = tool.InputSchema
        }).ToArray()
    };

    public async Task<object> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!_toolsByName.TryGetValue(name, out var tool))
        {
            return ErrorResult($"未知工具：{name}");
        }

        try
        {
            var payload = await tool.Handler(arguments, cancellationToken).ConfigureAwait(false);
            var structuredContent = JsonSerializer.SerializeToElement(payload, ProtocolJson.Options);
            return new
            {
                content = new[]
                {
                    new { type = "text", text = JsonSerializer.Serialize(payload, ProtocolJson.PrettyOptions) }
                },
                structuredContent,
                isError = false
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidOperationException or
            InvalidDataException or
            FileNotFoundException or
            IOException or
            UnauthorizedAccessException or
            KeyNotFoundException or
            TimeoutException or
            JsonException or
            JsonRpcException)
        {
            Console.Error.WriteLine($"[GAssetForge MCP] 工具 {name} 执行失败：{exception.Message}");
            return ErrorResult(exception.Message);
        }
    }

    public static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static object ErrorResult(string message) => new
    {
        content = new[] { new { type = "text", text = message } },
        isError = true
    };
}

internal static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = Create(writeIndented: false);

    public static JsonSerializerOptions PrettyOptions { get; } = Create(writeIndented: true);

    private static JsonSerializerOptions Create(bool writeIndented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
