using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using GAssetForge.App.Services;
using GAssetForge.Core;

namespace GAssetForge.App.Services.AgentRuntime;

internal static class CodexProcessLaunch
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal static ProcessStartInfo CreateBaseStartInfo(
        AgentSessionStartOptions options,
        string? applicationDirectory = null,
        string? pathEnvironment = null,
        string? applicationDataDirectory = null,
        string? localApplicationDataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);

        var workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(options.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Agent 工作目录不存在：{workingDirectory}");
        }

        ValidateMcpOptions(options);
        var resolverPathEnvironment = pathEnvironment;
        if (resolverPathEnvironment is null &&
            TryGetEnvironmentVariable(options.EnvironmentVariables, "PATH", out var configuredPath))
        {
            resolverPathEnvironment = configuredPath ?? string.Empty;
        }

        var executableLaunch = CodexExecutableResolver.Resolve(
            options.ExecutablePath,
            applicationDirectory,
            resolverPathEnvironment,
            applicationDataDirectory: applicationDataDirectory,
            localApplicationDataDirectory: localApplicationDataDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = executableLaunch.ProcessFileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConfigureUtf8Redirection(startInfo);

        foreach (var prefixArgument in executableLaunch.PrefixArguments)
        {
            startInfo.ArgumentList.Add(prefixArgument);
        }

        foreach (var environmentVariable in options.EnvironmentVariables)
        {
            ValidateEnvironmentVariableName(environmentVariable.Key);
            startInfo.Environment[environmentVariable.Key] = environmentVariable.Value;
        }

        if (!string.IsNullOrWhiteSpace(options.McpBearerTokenEnvironmentVariableName))
        {
            startInfo.Environment[options.McpBearerTokenEnvironmentVariableName] = options.McpBearerToken;
        }

        return startInfo;
    }

    internal static void ConfigureUtf8Redirection(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.RedirectStandardInput)
        {
            startInfo.StandardInputEncoding = Utf8NoBom;
        }

        if (startInfo.RedirectStandardOutput)
        {
            startInfo.StandardOutputEncoding = Utf8NoBom;
        }

        if (startInfo.RedirectStandardError)
        {
            startInfo.StandardErrorEncoding = Utf8NoBom;
        }
    }

    internal static void AddMcpConfiguration(
        ProcessStartInfo startInfo,
        AgentSessionStartOptions options)
    {
        if (options.McpEndpoint is null)
        {
            return;
        }

        var serverPrefix = $"mcp_servers.{ProductIdentity.McpServerName}";
        AddConfig(startInfo, $"{serverPrefix}.url={TomlString(options.McpEndpoint.AbsoluteUri)}");
        AddConfig(
            startInfo,
            $"{serverPrefix}.bearer_token_env_var={TomlString(options.McpBearerTokenEnvironmentVariableName!)}");
        AddConfig(startInfo, $"{serverPrefix}.required=true");
        AddConfig(
            startInfo,
            $"{serverPrefix}.tool_timeout_sec={GenerationMcpToolPolicy.ToolTimeoutSeconds}");
        AddConfig(
            startInfo,
            $"{serverPrefix}.enabled_tools={GenerationMcpToolPolicy.BuildTomlToolArray()}");
        // Clicking "Start Generation" is the user's explicit authorization for this app-owned,
        // loopback-only workflow. Codex MCP approvals are configured separately from the general
        // approval_policy; without this override app-server asks the host to approve every tool
        // call and a non-interactive batch can end as "user rejected MCP tool call".
        AddConfig(
            startInfo,
            $"{serverPrefix}.default_tools_approval_mode={TomlString(GenerationMcpToolPolicy.DefaultToolsApprovalMode)}");
    }

    internal static void AddSandboxConfiguration(
        ProcessStartInfo startInfo,
        AgentSessionStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.AllowSandboxNetworkAccess)
        {
            return;
        }

        if (!string.Equals(options.SandboxMode, "workspace-write", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "GAssetForge 只允许在 workspace-write sandbox 内为受管 Image API CLI 开启出站网络。");
        }

        AddConfig(startInfo, "sandbox_workspace_write.network_access=true");
    }

    internal static void AddConfig(ProcessStartInfo startInfo, string value)
    {
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(value);
    }

    internal static string Redact(string message, AgentSessionStartOptions? options)
    {
        if (options is null || string.IsNullOrEmpty(options.McpBearerToken))
        {
            return message;
        }

        return message.Replace(options.McpBearerToken, "[REDACTED]", StringComparison.Ordinal);
    }

    internal static string TomlString(string value) => JsonSerializer.Serialize(value);

    private static void ValidateMcpOptions(AgentSessionStartOptions options)
    {
        var hasEndpoint = options.McpEndpoint is not null;
        var hasVariable = !string.IsNullOrWhiteSpace(options.McpBearerTokenEnvironmentVariableName);
        var hasToken = !string.IsNullOrWhiteSpace(options.McpBearerToken);
        if (hasEndpoint != hasVariable || hasEndpoint != hasToken)
        {
            throw new ArgumentException("MCP Endpoint、Bearer Token 环境变量名和值必须成组提供。", nameof(options));
        }

        if (hasVariable)
        {
            ValidateEnvironmentVariableName(options.McpBearerTokenEnvironmentVariableName!);
        }

        if (options.McpEndpoint is not null &&
            !string.Equals(options.McpEndpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.McpEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("MCP Endpoint 必须使用 HTTP 或 HTTPS。", nameof(options));
        }
    }

    private static void ValidateEnvironmentVariableName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('=', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("环境变量名无效。", nameof(name));
        }
    }

    private static bool TryGetEnvironmentVariable(
        IReadOnlyDictionary<string, string?> environmentVariables,
        string name,
        out string? value)
    {
        var found = false;
        value = null;
        foreach (var environmentVariable in environmentVariables)
        {
            if (string.Equals(environmentVariable.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = environmentVariable.Value;
                found = true;
            }
        }

        return found;
    }
}
