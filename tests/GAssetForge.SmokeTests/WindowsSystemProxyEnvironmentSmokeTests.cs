using GAssetForge.App.Services;

namespace GAssetForge.SmokeTests;

internal static class WindowsSystemProxyEnvironmentSmokeTests
{
    public static void Run()
    {
        InheritsEnabledManualProxyForBothProtocols();
        PreservesExplicitProxyAndBypassSettings();
        MapsProtocolSpecificProxyAndSafeBypasses();
        RejectsDisabledEmptyAndUnsafeSettings();
        FingerprintTracksEffectiveNetworkWithoutExposingSecrets();
    }

    private static void InheritsEnabledManualProxyForBothProtocols()
    {
        var variables = EmptyEnvironment();
        WindowsSystemProxyEnvironment.ApplyFallback(variables,
            new WindowsManualProxySettings(true, "127.0.0.1:8001", "<local>"));
        Equal("http://127.0.0.1:8001", variables.GetValueOrDefault("HTTP_PROXY"),
            "Agent 必须继承用户已启用的 Windows 手动代理，确保依赖代理环境变量的子进程获得一致配置。");
        Equal("http://127.0.0.1:8001", variables.GetValueOrDefault("HTTPS_PROXY"),
            "通用 Windows 代理也必须用于 HTTPS 图片生成请求。");
        AssertLoopbackBypass(variables);
        var first = string.Join("|", variables.OrderBy(pair => pair.Key).Select(pair => pair.Value));
        WindowsSystemProxyEnvironment.ApplyFallback(variables,
            new WindowsManualProxySettings(true, "127.0.0.1:9001", null));
        Equal(first, string.Join("|", variables.OrderBy(pair => pair.Key).Select(pair => pair.Value)),
            "重复应用不得覆盖已有环境代理或重复追加 bypass。");
    }

    private static void PreservesExplicitProxyAndBypassSettings()
    {
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
        {
            var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [name] = "http://explicit.example:8123",
                ["NO_PROXY"] = "existing.example,10.0.0.0/8"
            };
            WindowsSystemProxyEnvironment.ApplyFallback(variables,
                new WindowsManualProxySettings(true, "127.0.0.1:8001", "system.example"));
            Equal("http://explicit.example:8123", variables[name], "显式代理必须保持最高优先级。");
            True(!variables.Values.Any(value => value?.Contains("8001", StringComparison.Ordinal) == true),
                "只要有一个显式代理，不得再注入系统代理导致混用。");
            True(variables["NO_PROXY"]!.StartsWith("existing.example,10.0.0.0/8", StringComparison.Ordinal),
                "用户已有 NO_PROXY 内容必须原样保留。");
            True(!variables["NO_PROXY"]!.Contains("system.example", StringComparison.Ordinal),
                "显式代理环境不得混入 Windows 代理的 bypass。");
            AssertLoopbackBypass(variables);
        }

        var bypassOnly = EmptyEnvironment();
        bypassOnly["NO_PROXY"] = "custom.example";
        WindowsSystemProxyEnvironment.ApplyFallback(bypassOnly,
            new WindowsManualProxySettings(true, "proxy.example:8000", "*.internal.example"));
        Equal("http://proxy.example:8000", bypassOnly["HTTP_PROXY"],
            "仅 NO_PROXY 不是显式代理端点，仍可继承系统手动代理。");
        True(bypassOnly["NO_PROXY"]!.Contains("custom.example", StringComparison.Ordinal), "保留用户 bypass。");
    }

    private static void MapsProtocolSpecificProxyAndSafeBypasses()
    {
        var variables = EmptyEnvironment();
        WindowsSystemProxyEnvironment.ApplyFallback(variables,
            new WindowsManualProxySettings(true,
                "http=proxy.example:8080;https=https://secure.example:8443;ftp=unused.example:21;socks=unused.example:1080",
                "<local>;*.internal.example;intranet.example;10.0.0.8;http://not-a-host;bad?.example;localhost"));
        Equal("http://proxy.example:8080", variables["HTTP_PROXY"], "保留 HTTP 独立端口。");
        Equal("https://secure.example:8443", variables["HTTPS_PROXY"], "保留 HTTPS 代理协议和独立端口。");
        True(variables.GetValueOrDefault("ALL_PROXY") is null, "不得把 FTP/SOCKS 冒充通用 HTTP 代理。");
        var bypasses = variables["NO_PROXY"]!.Split(',');
        True(bypasses.Contains(".internal.example"), "可表示的域名通配应映射到 NO_PROXY 后缀。");
        True(bypasses.Contains("intranet.example") && bypasses.Contains("10.0.0.8"), "保留精确主机和 IP 绕过项。");
        True(!bypasses.Any(value => value.Contains('<') || value.Contains('?') || value.Contains("://", StringComparison.Ordinal)),
            "不得把 PAC/WinINet 专用表达式错误注入 NO_PROXY。");
        AssertLoopbackBypass(variables);

        var httpOnly = EmptyEnvironment();
        WindowsSystemProxyEnvironment.ApplyFallback(httpOnly,
            new WindowsManualProxySettings(true, "http=localhost:8000", null));
        Equal("http://localhost:8000", httpOnly["HTTP_PROXY"], "支持只有 HTTP 的系统手动代理。");
        True(httpOnly.GetValueOrDefault("HTTPS_PROXY") is null, "不得猜测缺失的协议代理。");
    }

    private static void RejectsDisabledEmptyAndUnsafeSettings()
    {
        foreach (var settings in new WindowsManualProxySettings?[]
        {
            null,
            new(false, "localhost:8000", null),
            new(true, null, null),
            new(true, "", null),
            new(true, "file:///proxy.pac", null),
            new(true, "http://proxy.example/path", null),
            new(true, "http://proxy.example/?secret=fixture", null),
            new(true, "http://user:secret@proxy.example:8000", null),
            new(true, "proxy.example:99999", null),
            new(true, "proxy host:8000", null),
            new(true, "http://*:8000", null),
            new(true, "http=broken:port;https=broken:port", null),
            new(true, "socks=localhost:1080", null)
        })
        {
            var variables = EmptyEnvironment();
            WindowsSystemProxyEnvironment.ApplyFallback(variables, settings);
            True(variables.GetValueOrDefault("HTTP_PROXY") is null && variables.GetValueOrDefault("HTTPS_PROXY") is null,
                "关闭、PAC、空或不安全配置不得成为子进程 HTTP 代理。");
            AssertLoopbackBypass(variables);
        }
    }

    private static void FingerprintTracksEffectiveNetworkWithoutExposingSecrets()
    {
        var first = EmptyEnvironment();
        var second = EmptyEnvironment();
        WindowsSystemProxyEnvironment.ApplyFallback(first, new WindowsManualProxySettings(true, "localhost:8001", null));
        WindowsSystemProxyEnvironment.ApplyFallback(second, new WindowsManualProxySettings(true, "localhost:8002", null));
        True(ProcessEnvironmentSnapshot.ComputeEnvironmentFingerprint(first) != ProcessEnvironmentSnapshot.ComputeEnvironmentFingerprint(second),
            "Windows 代理修改后，环境指纹必须变化以触发运行时重建。");
        const string secret = "proxy-secret-fixture";
        var invalidSettings = new WindowsManualProxySettings(true, $"http://user:{secret}@localhost:8001", null);
        True(!invalidSettings.ToString().Contains(secret, StringComparison.Ordinal),
            "系统代理设置的安全摘要不得泄露意外嵌入的凭据。");
        first["HTTPS_PROXY"] = $"http://user:{secret}@localhost:8001";
        var fingerprint = ProcessEnvironmentSnapshot.ComputeEnvironmentFingerprint(first);
        Equal(64, fingerprint.Length, "指纹只返回固定长度 SHA-256。");
        True(!fingerprint.Contains(secret, StringComparison.Ordinal), "环境指纹不得泄露显式代理凭据。");
    }

    private static Dictionary<string, string?> EmptyEnvironment() => new(StringComparer.OrdinalIgnoreCase);

    private static void AssertLoopbackBypass(IDictionary<string, string?> variables)
    {
        True(variables.TryGetValue("NO_PROXY", out var value), "必须给本地 MCP 配置 NO_PROXY。");
        var bypasses = value!.Split(',');
        foreach (var host in new[] { "localhost", "127.0.0.1", "::1" })
        {
            True(bypasses.Contains(host, StringComparer.OrdinalIgnoreCase), "本地 MCP 回环地址必须绕过代理。");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), message);
}
