using System.IO;

namespace GAssetForge.App.Services;

internal static class ProcessEnvironmentSnapshot
{
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
}
