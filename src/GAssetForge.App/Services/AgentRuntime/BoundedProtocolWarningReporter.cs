namespace GAssetForge.App.Services.AgentRuntime;

internal sealed class BoundedProtocolWarningReporter
{
    private readonly object _gate = new();
    private readonly Action<string> _emit;
    private readonly string _sourceLabel;
    private readonly Dictionary<string, WarningState> _states = new(StringComparer.Ordinal);

    internal BoundedProtocolWarningReporter(string sourceLabel, Action<string> emit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLabel);
        _sourceLabel = sourceLabel.Trim();
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
    }

    internal void Report(SafeJsonLineDiagnostic diagnostic)
    {
        string? message = null;
        lock (_gate)
        {
            if (!_states.TryGetValue(diagnostic.Category, out var state))
            {
                state = new WarningState();
                _states.Add(diagnostic.Category, state);
            }

            state.Total++;
            if (state.Total == 1)
            {
                state.LastReportedTotal = 1;
                message = diagnostic.Message;
            }
            else if (state.Total == state.NextSummaryTotal)
            {
                state.LastReportedTotal = state.Total;
                state.NextSummaryTotal = NextSummaryTotal(state.NextSummaryTotal);
                message = BuildSummary(diagnostic.Category, state.Total);
            }
        }

        if (message is not null)
        {
            _emit(message);
        }
    }

    internal void Flush()
    {
        List<string>? summaries = null;
        lock (_gate)
        {
            foreach (var (category, state) in _states)
            {
                if (state.Total <= 1 || state.Total == state.LastReportedTotal)
                {
                    continue;
                }

                state.LastReportedTotal = state.Total;
                summaries ??= [];
                summaries.Add(BuildSummary(category, state.Total));
            }
        }

        if (summaries is null)
        {
            return;
        }

        foreach (var summary in summaries)
        {
            _emit(summary);
        }
    }

    private string BuildSummary(string category, long total) =>
        string.Join(
            "; ",
            $"{_sourceLabel} 非协议诊断汇总",
            $"category={category}",
            $"total={total}",
            $"suppressed={total - 1}");

    private static long NextSummaryTotal(long current) =>
        current > long.MaxValue / 10 ? long.MaxValue : current * 10;

    private sealed class WarningState
    {
        internal long Total { get; set; }

        internal long LastReportedTotal { get; set; }

        internal long NextSummaryTotal { get; set; } = 10;
    }
}
