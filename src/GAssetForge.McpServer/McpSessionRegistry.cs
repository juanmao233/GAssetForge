using System.Collections.Concurrent;
using GAssetForge.Core.Generation;

namespace GAssetForge.McpServer;

internal sealed class McpSessionRegistry
{
    public static readonly TimeSpan DefaultSessionTtl = TimeSpan.FromSeconds(
        GenerationMcpRuntimePolicy.HttpSessionIdleTimeoutSeconds);

    private readonly ConcurrentDictionary<string, McpSessionState> _sessions =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _sessionTtl;

    public McpSessionRegistry(TimeProvider? timeProvider = null, TimeSpan? sessionTtl = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionTtl = sessionTtl ?? DefaultSessionTtl;
        if (_sessionTtl <= TimeSpan.Zero || _sessionTtl > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessionTtl),
                "MCP HTTP 会话 TTL 必须大于 0 且不超过 24 小时。");
        }
    }

    public int Count => _sessions.Count;

    public McpSessionState CreateSession() => new(GetUtcNow());

    public string Add(McpSessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.RecordActivity(GetUtcNow());

        string sessionId;
        do
        {
            sessionId = Guid.NewGuid().ToString("N");
        }
        while (!_sessions.TryAdd(sessionId, session));

        return sessionId;
    }

    public bool TryGetActive(string sessionId, out McpSessionState? session)
    {
        if (!_sessions.TryGetValue(sessionId, out var candidate))
        {
            session = null;
            return false;
        }

        if (candidate.TryRecordActivity(GetUtcNow(), _sessionTtl))
        {
            session = candidate;
            return true;
        }

        RemoveExact(sessionId, candidate);
        session = null;
        return false;
    }

    public bool Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    public int RemoveExpiredSessions()
    {
        var now = GetUtcNow();
        var removed = 0;
        foreach (var pair in _sessions)
        {
            if (pair.Value.TryExpire(now, _sessionTtl) && RemoveExact(pair.Key, pair.Value))
            {
                removed++;
            }
        }

        return removed;
    }

    public McpSessionState[] GetActiveSessions()
    {
        RemoveExpiredSessions();
        return _sessions.Values.ToArray();
    }

    private bool RemoveExact(string sessionId, McpSessionState session) =>
        ((ICollection<KeyValuePair<string, McpSessionState>>)_sessions)
        .Remove(new KeyValuePair<string, McpSessionState>(sessionId, session));

    private DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();
}
