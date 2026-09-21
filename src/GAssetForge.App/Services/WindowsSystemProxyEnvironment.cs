using Microsoft.Win32;
using System.IO;
using System.Net;
using System.Security;

namespace GAssetForge.App.Services;

internal sealed record WindowsManualProxySettings(bool Enabled, string? Server, string? BypassList)
{
    public override string ToString() =>
        $"Enabled={Enabled}; ManualProxyConfigured={!string.IsNullOrWhiteSpace(Server)}; BypassConfigured={!string.IsNullOrWhiteSpace(BypassList)}";
}

internal static class WindowsSystemProxyEnvironment
{
    private static readonly string[] ProxyVariableNames = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"];
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1"];

    internal static WindowsManualProxySettings? ReadCurrentUserSettings()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // Read only the user's enabled manual WinINet configuration. A PAC URL or automatic
            // discovery cannot safely be translated into one static proxy environment variable.
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: false);
            return key?.GetValue("ProxyEnable") is int enabled && enabled == 1
                ? new WindowsManualProxySettings(true,
                    key.GetValue("ProxyServer") as string,
                    key.GetValue("ProxyOverride") as string)
                : null;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Reading a locked-down user's optional proxy configuration must not prevent startup.
            // Do not log registry values: a malformed endpoint may contain credentials.
            return null;
        }
    }

    internal static void ApplyFallback(
        IDictionary<string, string?> variables,
        WindowsManualProxySettings? settings)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var inherited = false;
        if (!HasExplicitProxy(variables) && settings is { Enabled: true } && !string.IsNullOrWhiteSpace(settings.Server))
        {
            foreach (var pair in ParseManualProxy(settings.Server))
            {
                variables[pair.Key] = pair.Value;
                // Lowercase aliases matter to Python and other cross-platform child tools. With
                // the Windows snapshot's case-insensitive dictionary this is the same entry.
                variables[pair.Key.ToLowerInvariant()] = pair.Value;
                inherited = true;
            }
        }

        var bypassValues = variables
            .Where(pair => string.Equals(pair.Key, "NO_PROXY", StringComparison.OrdinalIgnoreCase) &&
                           !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Value!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var existingHosts = bypassValues
            .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additionalHosts = LoopbackHosts.AsEnumerable();
        if (inherited)
        {
            additionalHosts = additionalHosts.Concat(ParseBypassList(settings!.BypassList));
        }

        foreach (var host in additionalHosts)
        {
            if (existingHosts.Add(host))
            {
                bypassValues.Add(host);
            }
        }

        // Preserve explicit NO_PROXY text while ensuring the local authenticated MCP server is
        // never accidentally sent through a remote proxy, including when an explicit proxy wins.
        var bypass = string.Join(',', bypassValues);
        variables["NO_PROXY"] = bypass;
        variables["no_proxy"] = bypass;
    }

    internal static bool HasExplicitProxy(IEnumerable<KeyValuePair<string, string?>> variables) =>
        variables.Any(pair => !string.IsNullOrWhiteSpace(pair.Value) &&
                              ProxyVariableNames.Contains(pair.Key, StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<KeyValuePair<string, string>> ParseManualProxy(string server)
    {
        if (!server.Contains('='))
        {
            if (TryNormalizeProxy(server, out var proxy))
            {
                yield return new("HTTP_PROXY", proxy);
                yield return new("HTTPS_PROXY", proxy);
            }

            yield break;
        }

        foreach (var entry in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var protocol = entry[..separator].Trim();
            var variable = protocol.Equals("http", StringComparison.OrdinalIgnoreCase) ? "HTTP_PROXY"
                : protocol.Equals("https", StringComparison.OrdinalIgnoreCase) ? "HTTPS_PROXY" : null;
            if (variable is not null && TryNormalizeProxy(entry[(separator + 1)..], out var proxy))
            {
                yield return new(variable, proxy);
            }
        }
    }

    private static bool TryNormalizeProxy(string endpoint, out string proxy)
    {
        proxy = string.Empty;
        endpoint = endpoint.Trim();
        if (endpoint.Length == 0 || endpoint.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return false;
        }

        var uriText = endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : "http://" + endpoint;
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            uri.Port is <= 0 or > 65535 ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        proxy = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        return true;
    }

    private static IEnumerable<string> ParseBypassList(string? bypassList)
    {
        foreach (var entry in (bypassList ?? string.Empty).Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry == "*")
            {
                yield return entry;
                continue;
            }

            // <local>, arbitrary wildcards and PAC expressions have no faithful static NO_PROXY
            // equivalent. Map only exact hosts/IPs and the standard *.example.com suffix form.
            var host = entry.StartsWith("*.", StringComparison.Ordinal) ? entry[1..] : entry;
            var validationHost = host.StartsWith('.') ? host[1..] : host;
            if (validationHost.Length > 0 &&
                (IPAddress.TryParse(validationHost, out _) || Uri.CheckHostName(validationHost) == UriHostNameType.Dns))
            {
                yield return host;
            }
        }
    }
}
