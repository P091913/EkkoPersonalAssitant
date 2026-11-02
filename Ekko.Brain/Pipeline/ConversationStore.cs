using System.Collections.Concurrent;

namespace Ekko.Brain.Pipeline;

/// <summary>Something Ekko asked last turn ("the show or the movie?") that the next message in the same conversation answers.</summary>
public abstract record Pending;

/// <summary>
/// At most one pending question per conversation, alive for a few minutes. Taking it removes it:
/// the next message either answers it or abandons it, and a handler that needs to ask again sets a new one.
/// </summary>
// shortcut: in memory, so a restart drops pending questions; persist it if they ever need to outlive minutes.
// Abandoned conversations' entries also linger until that conversation speaks again - fine for a handful of channels.
public sealed class ConversationStore(TimeProvider clock, TimeSpan ttl)
{
    private readonly ConcurrentDictionary<string, (Pending Pending, DateTimeOffset Expires)> _pending = new();

    public void Set(string conversationId, Pending pending) => _pending[conversationId] = (pending, clock.GetUtcNow() + ttl);

    public Pending? Take(string conversationId) =>
        _pending.TryRemove(conversationId, out var entry) && entry.Expires > clock.GetUtcNow() ? entry.Pending : null;
}
