using System.Net;
using GAssetForge.Core;

namespace GAssetForge.McpServer;

internal enum McpTransport
{
    Stdio,
    Http
}

internal sealed record ServerOptions(
    string DataDirectory,
    McpTransport Transport,
    IPAddress HostAddress,
    int Port,
    string ExpectedAgent,
    string? BearerTokenEnvironmentVariableName,
    string? BearerToken)
{
    private const int DefaultHttpPort = 43120;
    private const int MinimumBearerTokenLength = 32;

    public string SettingsFilePath => Path.Combine(DataDirectory, "Settings", "user-settings.json");

    public string DefaultAssetRoot => Path.Combine(DataDirectory, "Assets");

    public string GenerationStorageRoot => Path.Combine(DataDirectory, "Generation");

    public bool UsesHttp => Transport == McpTransport.Http;

    public string Endpoint => HostAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        ? $"http://[{HostAddress}]:{Port}/mcp"
        : $"http://{HostAddress}:{Port}/mcp";

    public static ServerOptions Parse(string[] args)
    {
        var configuredDirectory = Environment.GetEnvironmentVariable(ProductIdentity.DataRootEnvironmentVariableName)
            ?? Environment.GetEnvironmentVariable(ProductIdentity.LegacyDataRootEnvironmentVariableName);
        var transport = McpTransport.Stdio;
        var hostAddress = IPAddress.Loopback;
        var port = DefaultHttpPort;
        var expectedAgent = "codex";
        string? bearerTokenEnvironmentVariableName = null;

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException($"{option} 必须提供参数值。");
            }

            var value = args[index];
            switch (option)
            {
                case "--data-root":
                    configuredDirectory = value;
                    break;

                case "--transport":
                    transport = value.ToLowerInvariant() switch
                    {
                        "stdio" => McpTransport.Stdio,
                        "http" => McpTransport.Http,
                        _ => throw new ArgumentException("--transport 只支持 stdio 或 http。")
                    };
                    break;

                case "--host":
                    if (!IPAddress.TryParse(value, out hostAddress) || !IPAddress.IsLoopback(hostAddress))
                    {
                        throw new ArgumentException("--host 只能使用 loopback 地址（例如 127.0.0.1 或 ::1）。");
                    }

                    break;

                case "--port":
                    if (!int.TryParse(value, out port) || port is < 1024 or > 65535)
                    {
                        throw new ArgumentException("--port 必须是 1024 到 65535 之间的整数。");
                    }

                    break;

                case "--expected-agent":
                    expectedAgent = value.Trim().ToLowerInvariant();
                    if (expectedAgent is not ("codex" or "opencode"))
                    {
                        throw new ArgumentException("--expected-agent 只支持 codex 或 opencode。");
                    }

                    break;

                case "--token-env":
                    bearerTokenEnvironmentVariableName = ValidateEnvironmentVariableName(value);
                    break;

                default:
                    throw new ArgumentException($"未知启动参数：{option}");
            }
        }

        configuredDirectory ??= ResolveDefaultDataDirectory();

        if (!Path.IsPathFullyQualified(configuredDirectory))
        {
            throw new ArgumentException("数据根目录必须是绝对路径。");
        }

        var dataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredDirectory));
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(Path.Combine(dataDirectory, "Settings"));
        Directory.CreateDirectory(Path.Combine(dataDirectory, "Assets"));
        Directory.CreateDirectory(Path.Combine(dataDirectory, "Generation"));

        string? bearerToken = null;
        if (transport == McpTransport.Http)
        {
            if (string.IsNullOrWhiteSpace(bearerTokenEnvironmentVariableName))
            {
                throw new ArgumentException("HTTP 传输必须通过 --token-env 指定 Bearer Token 环境变量名。");
            }

            bearerToken = Environment.GetEnvironmentVariable(bearerTokenEnvironmentVariableName);
            ValidateBearerToken(bearerToken);
        }

        return new ServerOptions(
            dataDirectory,
            transport,
            hostAddress,
            port,
            expectedAgent,
            bearerTokenEnvironmentVariableName,
            bearerToken);
    }

    private static string ValidateEnvironmentVariableName(string value)
    {
        var candidate = value.Trim();
        if (!string.Equals(candidate, value, StringComparison.Ordinal) ||
            candidate.Length is 0 or > 128 ||
            candidate.Contains('=') ||
            candidate.Contains('\0'))
        {
            throw new ArgumentException("--token-env 必须是有效的环境变量名。");
        }

        return candidate;
    }

    private static void ValidateBearerToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < MinimumBearerTokenLength)
        {
            throw new InvalidOperationException(
                $"--token-env 指定的环境变量必须包含至少 {MinimumBearerTokenLength} 个字符的 Bearer Token。");
        }

        if (token.Any(character => !IsBearerTokenCharacter(character)))
        {
            throw new InvalidOperationException("--token-env 指定的 Bearer Token 包含不受支持的字符。");
        }
    }

    private static bool IsBearerTokenCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~' or '+' or '/' or '=';

    private static string ResolveDefaultDataDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var canonicalRoot = Path.Combine(localApplicationData, ProductIdentity.DataDirectoryName, "Desktop");
        var legacyRoot = Path.Combine(localApplicationData, ProductIdentity.LegacyDataDirectoryName, "Desktop");
        return Directory.Exists(canonicalRoot) || !Directory.Exists(legacyRoot)
            ? canonicalRoot
            : legacyRoot;
    }
}
