using System.Text;
using System.Text.Json;

namespace GAssetForge.McpServer;

internal sealed class StdioMcpServer(McpRequestDispatcher dispatcher)
{
    private const int MaximumMessageCharacters = 4 * 1024 * 1024;

    private readonly JsonRpcMessageHandler _messageHandler = new(dispatcher);
    private readonly McpSessionState _session = new();

    private readonly StreamReader _input = new(
        Console.OpenStandardInput(),
        new UTF8Encoding(false),
        detectEncodingFromByteOrderMarks: true,
        bufferSize: 4096,
        leaveOpen: true);

    private readonly StreamWriter _output = new(
        Console.OpenStandardOutput(),
        new UTF8Encoding(false),
        bufferSize: 4096,
        leaveOpen: true)
    {
        AutoFlush = true,
        NewLine = "\n"
    };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("[GAssetForge MCP] stdio server started.");

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Length > MaximumMessageCharacters)
            {
                await WriteAsync(JsonRpcMessageHandler.ErrorResponse(
                        null,
                        JsonRpcErrorCodes.InvalidRequest,
                        "JSON-RPC 消息超过 4 MiB 限制。"))
                    .ConfigureAwait(false);
                continue;
            }

            await ProcessLineAsync(line, cancellationToken).ConfigureAwait(false);
        }

        Console.Error.WriteLine("[GAssetForge MCP] stdin closed; server stopped.");
    }

    private async Task ProcessLineAsync(string line, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"[GAssetForge MCP] JSON parse error: {exception.Message}");
            await WriteAsync(JsonRpcMessageHandler.ErrorResponse(
                    null,
                    JsonRpcErrorCodes.ParseError,
                    "JSON 解析失败。"))
                .ConfigureAwait(false);
            return;
        }

        using (document)
        {
            var result = await _messageHandler.HandleAsync(
                    document.RootElement,
                    _session,
                    expectedAgent: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.HasResponse)
            {
                await WriteAsync(result.Response!).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteAsync(object message)
    {
        var json = JsonSerializer.Serialize(message, ProtocolJson.Options);
        await _output.WriteLineAsync(json).ConfigureAwait(false);
    }
}
