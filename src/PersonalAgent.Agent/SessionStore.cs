using System.Collections.Concurrent;
using Anthropic.Models.Messages;

namespace PersonalAgent.Agent;

public sealed class Session(string id)
{
    public string Id { get; } = id;
    public List<MessageParam> History { get; } = [];
    public SemaphoreSlim Lock { get; } = new(1, 1); // one request at a time per conversation
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// In-memory conversation store. Fine for learning and a single server instance.
/// For production with more than one instance, replace with Redis or a database.
/// </summary>
public sealed class SessionStore
{
    private const int MaxSessions = 1000;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(2);
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public Session GetOrCreate(string? id)
    {
        Evict();
        // Never trust a client-supplied id blindly: accept only our own GUID format.
        if (id is not null && Guid.TryParseExact(id, "N", out _) && _sessions.TryGetValue(id, out var existing))
            return existing;

        var session = new Session(Guid.NewGuid().ToString("N"));
        _sessions[session.Id] = session;
        return session;
    }

    private void Evict()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        foreach (var (key, s) in _sessions)
            if (s.LastUsedUtc < cutoff && s.Lock.CurrentCount == 1) _sessions.TryRemove(key, out _);

        while (_sessions.Count > MaxSessions)
        {
            var oldest = _sessions.Values.OrderBy(s => s.LastUsedUtc).First();
            _sessions.TryRemove(oldest.Id, out _);
        }
    }
}
