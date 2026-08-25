using System.Text;
using System.Text.Json;
using GAssetForge.Core.Generation;

namespace GAssetForge.McpServer;

internal sealed record DirectToolInvocationResult(int ExitCode, string Output);

/// <summary>
/// Executes the narrow task-worker tool surface as a one-shot local CLI call. This is the
/// supported manual handoff seam for an already-running Codex host that cannot dynamically load
/// the managed HTTP MCP configuration or inherit its ephemeral bearer-token environment variable.
/// </summary>
internal static class DirectToolInvocationExecutor
{
    internal const string InvokeToolOption = "--invoke-tool";
    internal const string ArgumentsBase64Option = "--arguments-base64";

    private const int MaximumDecodedArgumentsBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> AllowedTools =
    [
        "server_info",
        "get_generation_task",
        "claim_generation_task",
        "submit_generation_output",
        "run_image_delivery_fallback",
        "process_generation_output",
        "complete_generation_task",
        "fail_generation_task"
    ];

    public static bool IsRequested(IReadOnlyCollection<string> args) =>
        args.Contains(InvokeToolOption, StringComparer.Ordinal) ||
        args.Contains(ArgumentsBase64Option, StringComparer.Ordinal);

    public static async Task<DirectToolInvocationResult> ExecuteAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var invocation = Parse(args);
        var options = ServerOptions.Parse(invocation.ServerArguments);
        var contextResolver = new WorkspaceContextResolver(options);
        var taskStore = new JsonGenerationTaskStore(options.GenerationStorageRoot);
        var toolProvider = new GenerationToolProvider(
            taskStore,
            contextResolver,
            options,
            taskStore.StoreFilePath);
        var catalog = toolProvider.CreateCatalog();
        var result = await catalog.CallAsync(
                invocation.ToolName,
                invocation.Arguments,
                cancellationToken)
            .ConfigureAwait(false);
        var resultElement = JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
        var isError = resultElement.TryGetProperty("isError", out var isErrorElement) &&
                      isErrorElement.ValueKind == JsonValueKind.True;
        return new DirectToolInvocationResult(
            isError ? 2 : 0,
            JsonSerializer.Serialize(result, ProtocolJson.PrettyOptions));
    }

    internal static DirectToolInvocationRequest Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? toolName = null;
        string? argumentsBase64 = null;
        var serverArguments = new List<string>();

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException($"{option} 必须提供参数值。");
            }

            var value = args[++index];
            switch (option)
            {
                case InvokeToolOption:
                    if (toolName is not null)
                    {
                        throw new ArgumentException($"{InvokeToolOption} 只能提供一次。");
                    }

                    toolName = value.Trim();
                    break;

                case ArgumentsBase64Option:
                    if (argumentsBase64 is not null)
                    {
                        throw new ArgumentException($"{ArgumentsBase64Option} 只能提供一次。");
                    }

                    argumentsBase64 = value.Trim();
                    break;

                default:
                    serverArguments.Add(option);
                    serverArguments.Add(value);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new ArgumentException($"本地任务桥接必须提供 {InvokeToolOption}。");
        }

        if (!AllowedTools.Contains(toolName))
        {
            throw new ArgumentException(
                $"本地任务桥接不允许调用 {toolName}；只允许：{string.Join(", ", AllowedTools.Order())}。");
        }

        var arguments = DecodeArguments(argumentsBase64);
        return new DirectToolInvocationRequest(toolName, arguments, serverArguments.ToArray());
    }

    private static JsonElement DecodeArguments(string? argumentsBase64)
    {
        if (string.IsNullOrWhiteSpace(argumentsBase64))
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(argumentsBase64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                $"{ArgumentsBase64Option} 必须是 UTF-8 JSON 的标准 Base64。",
                exception);
        }

        if (bytes.Length > MaximumDecodedArgumentsBytes)
        {
            throw new ArgumentException(
                $"本地任务桥接参数不能超过 {MaximumDecodedArgumentsBytes / (1024 * 1024)} MiB。");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("本地任务桥接参数 JSON 必须是对象。");
            }

            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                $"{ArgumentsBase64Option} 解码后必须是有效 UTF-8 JSON。",
                exception);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }
}

internal sealed record DirectToolInvocationRequest(
    string ToolName,
    JsonElement Arguments,
    string[] ServerArguments);
