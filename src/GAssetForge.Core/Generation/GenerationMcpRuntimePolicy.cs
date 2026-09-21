namespace GAssetForge.Core.Generation;

/// <summary>
/// Shared timing contract for the desktop MCP client and the local MCP server.
/// The HTTP session is an idle lease, so it must outlive one maximum-duration
/// managed generation tool call plus enough time for transport and response work.
/// </summary>
public static class GenerationMcpRuntimePolicy
{
    public const int ToolTimeoutSeconds = 900;

    public const int HttpSessionIdleSafetyMarginSeconds = 300;

    public const int HttpSessionIdleTimeoutSeconds =
        ToolTimeoutSeconds + HttpSessionIdleSafetyMarginSeconds;
}
