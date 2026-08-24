using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GAssetForge.App.Services.AgentRuntime;

internal readonly record struct SafeJsonLineDiagnostic(string Category, string Message);

internal static class SafeJsonLineFramer
{
    internal const string InvalidSegmentCategory = "invalid-segment";
    internal const string NonObjectCategory = "non-object";

    internal static void ProcessObjects(
        string line,
        Action<JsonElement> messageHandler,
        Action<SafeJsonLineDiagnostic> diagnosticHandler)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(messageHandler);
        ArgumentNullException.ThrowIfNull(diagnosticHandler);

        var utf8Line = Encoding.UTF8.GetBytes(line);
        var offset = 0;
        var processedMessages = 0;
        while (offset < utf8Line.Length)
        {
            while (offset < utf8Line.Length && IsJsonWhitespace(utf8Line[offset]))
            {
                offset++;
            }

            if (offset >= utf8Line.Length)
            {
                return;
            }

            JsonDocument document;
            var messageOffset = offset;
            try
            {
                var reader = new Utf8JsonReader(utf8Line.AsSpan(offset), isFinalBlock: true, state: default);
                document = JsonDocument.ParseValue(ref reader);
                if (reader.BytesConsumed <= 0)
                {
                    throw new JsonException("Agent JSONL 解析器没有消费任何输入。");
                }

                offset += checked((int)reader.BytesConsumed);
            }
            catch (JsonException exception)
            {
                var recoveryOffset = FindNextTopLevelObjectStart(
                    utf8Line,
                    invalidSegmentOffset: offset,
                    searchOffset: offset + 1);
                var segmentEnd = recoveryOffset >= 0 ? recoveryOffset : utf8Line.Length;
                diagnosticHandler(new SafeJsonLineDiagnostic(
                    InvalidSegmentCategory,
                    BuildInvalidSegmentDiagnostic(
                        utf8Line.AsSpan(offset, segmentEnd - offset),
                        offset,
                        utf8Line.Length - offset,
                        processedMessages,
                        recoveryOffset,
                        exception)));
                if (recoveryOffset < 0)
                {
                    return;
                }

                offset = recoveryOffset;
                continue;
            }

            using (document)
            {
                var messageLength = offset - messageOffset;
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    diagnosticHandler(new SafeJsonLineDiagnostic(
                        NonObjectCategory,
                        BuildNonObjectDiagnostic(
                            utf8Line.AsSpan(messageOffset, messageLength),
                            messageOffset,
                            document.RootElement.ValueKind)));
                    continue;
                }

                messageHandler(document.RootElement);
                processedMessages++;
            }
        }
    }

    private static bool IsJsonWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static int FindNextTopLevelObjectStart(
        ReadOnlySpan<byte> bytes,
        int invalidSegmentOffset,
        int searchOffset)
    {
        if ((uint)invalidSegmentOffset >= (uint)bytes.Length ||
            (uint)searchOffset >= (uint)bytes.Length ||
            searchOffset <= invalidSegmentOffset)
        {
            return -1;
        }

        // Utf8JsonReader rejects messages deeper than its default maximum of 64 levels.
        // Once an invalid segment exceeds that bound, conservatively keep the remainder
        // quarantined instead of guessing that an inner object is a new protocol message.
        Span<byte> expectedClosers = stackalloc byte[64];
        var depth = 0;
        var isInsideString = false;
        var isEscaped = false;

        for (var index = invalidSegmentOffset; index < bytes.Length; index++)
        {
            var value = bytes[index];
            if (isInsideString)
            {
                if (isEscaped)
                {
                    isEscaped = false;
                    continue;
                }

                if (value == (byte)'\\')
                {
                    isEscaped = true;
                }
                else if (value == (byte)'"')
                {
                    isInsideString = false;
                }

                continue;
            }

            if (value == (byte)'"')
            {
                isInsideString = true;
                continue;
            }

            if (value is (byte)'{' or (byte)'[')
            {
                if (depth == 0 && index >= searchOffset && value == (byte)'{')
                {
                    return index;
                }

                if (depth == expectedClosers.Length)
                {
                    return -1;
                }

                expectedClosers[depth++] = value == (byte)'{' ? (byte)'}' : (byte)']';
                continue;
            }

            if (value != (byte)'}' && value != (byte)']')
            {
                continue;
            }

            if (depth > 0 && expectedClosers[depth - 1] == value)
            {
                depth--;
            }
        }

        return -1;
    }

    private static string BuildInvalidSegmentDiagnostic(
        ReadOnlySpan<byte> invalidSegment,
        int offset,
        int remainingBytes,
        int processedMessages,
        int recoveryOffset,
        JsonException exception)
    {
        return string.Join(
            "; ",
            "忽略 Agent JSONL 非协议片段",
            $"offset={offset}",
            $"segmentBytes={invalidSegment.Length}",
            $"remainingBytes={remainingBytes}",
            $"sha256={ComputeShortSha256(invalidSegment)}",
            $"processedMessages={processedMessages}",
            $"recoveryOffset={(recoveryOffset < 0 ? "none" : recoveryOffset)}",
            $"jsonLine={exception.LineNumber?.ToString() ?? "unknown"}",
            $"jsonByte={exception.BytePositionInLine?.ToString() ?? "unknown"}");
    }

    private static string BuildNonObjectDiagnostic(
        ReadOnlySpan<byte> messageBytes,
        int offset,
        JsonValueKind kind)
    {
        return string.Join(
            "; ",
            "忽略 Agent JSONL 非对象消息",
            $"offset={offset}",
            $"messageBytes={messageBytes.Length}",
            $"sha256={ComputeShortSha256(messageBytes)}",
            $"kind={kind}");
    }

    private static string ComputeShortSha256(ReadOnlySpan<byte> bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
