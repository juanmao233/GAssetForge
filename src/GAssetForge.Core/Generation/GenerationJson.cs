using System.Text.Json;
using System.Text.Json.Serialization;

namespace GAssetForge.Core.Generation;

public static class GenerationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string SerializeTask(GenerationTask task) =>
        JsonSerializer.Serialize(task, Options);

    public static GenerationTask DeserializeTask(string json) =>
        JsonSerializer.Deserialize<GenerationTask>(json, Options)
        ?? throw new InvalidDataException("生成任务 JSON 为空。");

    internal static T Clone<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        return JsonSerializer.Deserialize<T>(bytes, Options)
            ?? throw new InvalidDataException("生成任务 JSON 克隆结果为空。");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 64
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
