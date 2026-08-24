using System.Text.Json;
using System.Text.Json.Serialization;

namespace GAssetForge.Core.Blueprints;

public static class BlueprintJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string SerializeGraph(BlueprintGraph graph) =>
        JsonSerializer.Serialize(graph, Options);

    public static BlueprintGraph DeserializeGraph(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("蓝图 JSON 为空。");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.Number ||
                !schemaVersion.TryGetInt32(out _))
            {
                throw new InvalidDataException("蓝图 JSON 必须显式包含整数 schemaVersion。");
            }

            return JsonSerializer.Deserialize<BlueprintGraph>(json, Options)
                ?? throw new InvalidDataException("蓝图 JSON 为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("蓝图 JSON 格式无效。", exception);
        }
    }

    public static string SerializePlan(BlueprintExecutionPlan plan) =>
        JsonSerializer.Serialize(plan, Options);

    public static BlueprintGraph Clone(BlueprintGraph graph) =>
        DeserializeGraph(SerializeGraph(graph));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}
