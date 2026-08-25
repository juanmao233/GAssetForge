using System.Text.RegularExpressions;

namespace GAssetForge.Core.Generation;

public static partial class GenerationFailureClassifier
{
    private static readonly string[] RetryableGeometryCodes =
    [
        "tilemap_geometry_aspect_ratio_mismatch",
        "tilemap_geometry_rotation_mismatch",
        "tilemap_geometry_diamond_detected",
        "output_geometry_validation_failed"
    ];

    public static GenerationTaskFailure Classify(string failureMessage)
    {
        GenerationInputValidator.ValidateFailureMessage(failureMessage);
        var geometryCode = RetryableGeometryCodes.FirstOrDefault(code =>
            failureMessage.Contains(code, StringComparison.OrdinalIgnoreCase));
        if (geometryCode is not null)
        {
            return new GenerationTaskFailure
            {
                Message = failureMessage,
                Code = "output_geometry_validation_failed",
                SubCode = geometryCode == "output_geometry_validation_failed" ? null : geometryCode,
                Stage = "output.geometry_validation",
                Retryable = true
            };
        }

        if (failureMessage.StartsWith("image_delivery_fallback_failed", StringComparison.OrdinalIgnoreCase))
        {
            return new GenerationTaskFailure
            {
                Message = failureMessage,
                Code = "image_delivery_fallback_failed",
                Stage = "image.cli_fallback",
                Retryable = IsTransientDeliveryFailure(failureMessage)
            };
        }

        if (failureMessage.Contains("未被 Agent 领取或完成", StringComparison.Ordinal) ||
            failureMessage.Contains("未被 MCP 认领", StringComparison.Ordinal))
        {
            return new GenerationTaskFailure
            {
                Message = failureMessage,
                Code = "agent_turn_incomplete",
                Stage = "agent.dispatch",
                Retryable = true
            };
        }

        var explicitCode = ErrorCodeRegex().Match(failureMessage);
        return new GenerationTaskFailure
        {
            Message = failureMessage,
            Code = explicitCode.Success ? explicitCode.Groups[1].Value : "generation_failed",
            Stage = "generation",
            Retryable = false
        };
    }

    private static bool IsTransientDeliveryFailure(string message) =>
        message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("socket", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("HTTP 5", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?:errorCode|failureCode)\s*[=:]\s*([a-z0-9][a-z0-9._-]{0,127})", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorCodeRegex();
}
