using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GAssetForge.App.Services;

internal static class ProcessEnvironmentSnapshot
{
    private static readonly string[] AgentRuntimeEnvironmentVariableNames =
    [
        "OPENAI_API_KEY",
        "OPENAI_BASE_URL",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy",
        "no_proxy",
        "OPENAI_ORG_ID",
        "OPENAI_PROJECT_ID",
        "CODEX_HOME",
        "GASSETFORGE_PYTHON_EXECUTABLE",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        "REQUESTS_CA_BUNDLE"
    ];

    internal static string BuildFreshPathEnvironment()
    {
        var values = new List<string?>
        {
            Environment.GetEnvironmentVariable("PATH")
        };
        if (OperatingSystem.IsWindows())
        {
            try
            {
                values.Add(Environment.GetEnvironmentVariable(
                    "Path",
                    EnvironmentVariableTarget.Machine));
                values.Add(Environment.GetEnvironmentVariable(
                    "Path",
                    EnvironmentVariableTarget.User));
            }
            catch (Exception exception) when (exception is
                       System.Security.SecurityException or
                       UnauthorizedAccessException)
            {
            }
        }

        return MergePathValues(values);
    }

    internal static IReadOnlyDictionary<string, string?> BuildFreshAgentEnvironmentVariables()
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = BuildFreshPathEnvironment()
        };
        foreach (var name in AgentRuntimeEnvironmentVariableNames)
        {
            // Include null entries deliberately: ProcessStartInfo treats them as removals, so a
            // restarted Runtime cannot silently retain a stale inherited API/proxy setting.
            variables[name] = ReadFreshEnvironmentVariable(name);
        }

        return variables;
    }

    internal static string ComputeEnvironmentFingerprint(
        IReadOnlyDictionary<string, string?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var builder = new StringBuilder();
        foreach (var pair in variables.OrderBy(
                     pair => pair.Key,
                     OperatingSystem.IsWindows()
                         ? StringComparer.OrdinalIgnoreCase
                         : StringComparer.Ordinal))
        {
            var normalizedName = OperatingSystem.IsWindows()
                ? pair.Key.ToUpperInvariant()
                : pair.Key;
            AppendLengthPrefixed(builder, normalizedName);
            if (pair.Value is null)
            {
                builder.Append("-1:");
            }
            else
            {
                AppendLengthPrefixed(builder, pair.Value);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    internal static string MergePathValues(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var entries = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => value.Trim().Trim('"'))
            .Where(value => value.Length > 0)
            .Distinct(comparer);
        return string.Join(Path.PathSeparator, entries);
    }

    internal static string? ReadFreshEnvironmentVariable(string name)
    {
        var processValue = Environment.GetEnvironmentVariable(name);
        if (!OperatingSystem.IsWindows())
        {
            return NormalizeEnvironmentValue(processValue);
        }

        try
        {
            // Preserve the normal process override contract for launchers and secure sessions. If
            // the process did not inherit a value, read the live User/Machine settings so newly
            // installed API/proxy configuration can take effect without restarting the WPF host.
            return NormalizeEnvironmentValue(processValue) ??
                   NormalizeEnvironmentValue(Environment.GetEnvironmentVariable(
                       name,
                       EnvironmentVariableTarget.User)) ??
                   NormalizeEnvironmentValue(Environment.GetEnvironmentVariable(
                       name,
                       EnvironmentVariableTarget.Machine));
        }
        catch (Exception exception) when (exception is
                   System.Security.SecurityException or
                   UnauthorizedAccessException)
        {
            return NormalizeEnvironmentValue(processValue);
        }
    }

    private static string? NormalizeEnvironmentValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static void AppendLengthPrefixed(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value);
}
