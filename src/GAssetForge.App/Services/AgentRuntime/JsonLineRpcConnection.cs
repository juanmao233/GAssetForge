using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace GAssetForge.App.Services.AgentRuntime;

internal sealed class JsonLineRpcConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new { });

    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly BoundedProtocolWarningReporter _framingWarnings;
    private long _nextRequestId;
    private int _failed;

    internal JsonLineRpcConnection(
        TextReader reader,
        TextWriter writer,
        TimeSpan requestTimeout)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _requestTimeout = requestTimeout > TimeSpan.Zero
            ? requestTimeout
            : throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _framingWarnings = new BoundedProtocolWarningReporter(
            "Agent JSONL",
            message => ProtocolWarning?.Invoke(message));
    }

    internal event Action<string, JsonElement>? NotificationReceived;

    internal event Action<string>? ProtocolWarning;

    internal Func<string, JsonElement, CancellationToken, Task<object?>>? ServerRequestHandler
    {
        get;
        set;
    }

    internal int PendingRequestCount => _pending.Count;

    internal async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ThrowIfFailed();

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("无法分配 JSON-RPC 请求 ID。");
        }

        try
        {
            var request = new Dictionary<string, object?>
            {
                ["id"] = requestId,
                ["method"] = method
            };
            if (parameters is not null)
            {
                request["params"] = parameters;
            }

            await WriteMessageAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);

            using var timeoutCancellation = new CancellationTokenSource(_requestTimeout);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCancellation.Token,
                timeoutCancellation.Token);
            try
            {
                return await completion.Task.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                timeoutCancellation.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested &&
                !_disposeCancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"等待 Agent JSON-RPC `{method}` 响应超时（{_requestTimeout.TotalSeconds:0.#} 秒）。");
            }
        }
        catch
        {
            _pending.TryRemove(requestId, out _);
            throw;
        }
    }

    internal Task SendNotificationAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ThrowIfFailed();
        var notification = new Dictionary<string, object?>
        {
            ["method"] = method
        };
        if (parameters is not null)
        {
            notification["params"] = parameters;
        }

        return WriteMessageAsync(notification, cancellationToken);
    }

    internal async Task RunReadLoopAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        try
        {
            while (!linkedCancellation.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(linkedCancellation.Token).ConfigureAwait(false);
                if (line is null)
                {
                    throw new EndOfStreamException("Agent app-server 已关闭标准输出流。");
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ProcessLine(line);
            }
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fail(exception);
            throw;
        }
        finally
        {
            _framingWarnings.Flush();
        }
    }

    internal void ProcessLine(string line)
    {
        SafeJsonLineFramer.ProcessObjects(
            line,
            ProcessMessage,
            _framingWarnings.Report);
    }

    private void ProcessMessage(JsonElement root)
    {
        var hasMethod = root.TryGetProperty("method", out var methodElement) &&
                        methodElement.ValueKind == JsonValueKind.String;
        var hasId = root.TryGetProperty("id", out var idElement);

        if (hasMethod && hasId)
        {
            var parameters = root.TryGetProperty("params", out var parametersElement)
                ? parametersElement.Clone()
                : EmptyObject.Clone();
            _ = HandleServerRequestAsync(
                idElement.Clone(),
                methodElement.GetString() ?? "unknown",
                parameters);
            return;
        }

        if (hasMethod)
        {
            var parameters = root.TryGetProperty("params", out var parametersElement)
                ? parametersElement.Clone()
                : EmptyObject.Clone();
            NotificationReceived?.Invoke(methodElement.GetString()!, parameters);
            return;
        }

        if (!hasId || !TryReadNumericId(idElement, out var requestId) ||
            !_pending.TryRemove(requestId, out var completion))
        {
            ProtocolWarning?.Invoke("收到无法关联的 Agent JSON-RPC 响应。");
            return;
        }

        if (root.TryGetProperty("error", out var errorElement) &&
            errorElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            var code = errorElement.TryGetProperty("code", out var codeElement) &&
                       codeElement.TryGetInt32(out var parsedCode)
                ? parsedCode
                : -1;
            var message = errorElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? "Agent JSON-RPC 请求失败。"
                : "Agent JSON-RPC 请求失败。";
            completion.TrySetException(new AgentRpcException(code, message));
            return;
        }

        var result = root.TryGetProperty("result", out var resultElement)
            ? resultElement.Clone()
            : EmptyObject.Clone();
        completion.TrySetResult(result);
    }

    internal void Fail(Exception exception)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }

        foreach (var pending in _pending.ToArray())
        {
            if (_pending.TryRemove(pending.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private async Task HandleServerRequestAsync(
        JsonElement id,
        string method,
        JsonElement parameters)
    {
        try
        {
            var handler = ServerRequestHandler;
            if (handler is null)
            {
                await ReplyErrorAsync(
                        id,
                        -32601,
                        $"GAssetForge 尚未处理 app-server 请求：{method}")
                    .ConfigureAwait(false);
                return;
            }

            object? result;
            try
            {
                result = await handler(method, parameters, _disposeCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (NotSupportedException exception)
            {
                await ReplyErrorAsync(id, -32601, exception.Message).ConfigureAwait(false);
                return;
            }

            await WriteMessageAsync(
                    new Dictionary<string, object?>
                    {
                        ["id"] = id,
                        ["result"] = result ?? EmptyObject
                    },
                    _disposeCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            try
            {
                await ReplyErrorAsync(
                        id,
                        -32603,
                        $"GAssetForge 处理 app-server 请求失败：{exception.Message}")
                    .ConfigureAwait(false);
            }
            catch (Exception replyException)
            {
                ProtocolWarning?.Invoke($"回复 Agent 请求失败：{replyException.Message}");
            }
        }
    }

    private Task ReplyErrorAsync(JsonElement id, int code, string message) =>
        WriteMessageAsync(
            new Dictionary<string, object?>
            {
                ["id"] = id,
                ["error"] = new
                {
                    code,
                    message
                }
            },
            _disposeCancellation.Token);

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, SerializerOptions);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static bool TryReadNumericId(JsonElement id, out long value)
    {
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out value))
        {
            return true;
        }

        if (id.ValueKind == JsonValueKind.String &&
            long.TryParse(id.GetString(), out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failed) != 0)
        {
            throw new InvalidOperationException("Agent JSON-RPC 连接已经终止。");
        }
    }

    public ValueTask DisposeAsync()
    {
        _framingWarnings.Flush();
        _disposeCancellation.Cancel();
        Fail(new ObjectDisposedException(nameof(JsonLineRpcConnection)));
        _disposeCancellation.Dispose();
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
