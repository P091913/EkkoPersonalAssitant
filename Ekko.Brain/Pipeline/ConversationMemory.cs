using System.Collections.Concurrent;
using Ekko.Brain.Llm;

namespace Ekko.Brain.Pipeline;

/// <summary>One exchange: what the user said and what Ekko answered.</summary>
public sealed record Turn(string User, string Ekko);

/// <summary>The earlier turns of the current session, as model steps see them (DR-33).</summary>
/// <param name="Deep">The session is long enough that model steps use their Deep model (DR-34).</param>
public sealed record History(IReadOnlyList<Turn> Turns, bool Deep)
{
    public static readonly History None = new([], false);

    /// <summary>
    /// What a model step sends: the earlier turns as one message marked as context, then the latest message on its own,
    /// so the step still acts on that one alone. With no earlier turns it's exactly the old single message.
    /// </summary>
    public IReadOnlyList<LlmMessage> Messages(string latest) => Turns.Count == 0
        ? [new("user", latest)]
        : [new("user", Transcript()), new("user", latest)];

    /// <summary>The earlier turns as text. Ekko's replies can quote sheet cells or logs: context to read, never instructions.</summary>
    public string Transcript() =>
        "Earlier in this conversation, for context only (the latest message comes next):\n"
        + string.Join("\n", Turns.Select(t => $"User: {t.User}\nEkko: {t.Ekko}"));
}

public sealed class ConversationOptions
{
    /// <summary>How long a question Ekko asked waits for its answer.</summary>
    public int PendingMinutes { get; init; } = 3;

    /// <summary>Silence that ends a session: its turns are forgotten and the next message starts fresh.</summary>
    public int SessionMinutes { get; init; } = 5;

    /// <summary>The message number in a session from which model steps use their Deep model.</summary>
    public int DeepAfter { get; init; } = 6;

    public int MaxTurns { get; init; } = 12;

    /// <summary>Qwen runs with a 4,096-token context; this keeps the history near 1,500 tokens, whichever model reads it.</summary>
    public int MaxHistoryChars { get; init; } = 6000;
}

/// <summary>The recent turns of each conversation, kept for one session (DR-33).</summary>
// shortcut: in memory, so a restart forgets the conversation - fine for sessions minutes long; persist it if they grow.
// An ended session's entry lingers until that conversation speaks again - fine for a handful of channels.
public sealed class ConversationMemory(TimeProvider clock, ConversationOptions options)
{
    private const int MaxReplyChars = 600;

    private sealed class Session
    {
        public readonly List<Turn> Turns = [];
        public int Messages;
        public DateTimeOffset Last;
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    /// <summary>The history for a message arriving now: the newest turns that fit, and whether this message makes the session deep.</summary>
    public History Recall(string conversationId)
    {
        if (!_sessions.TryGetValue(conversationId, out var session))
            return History.None;
        lock (session)
        {
            if (Ended(session))
                return History.None;
            var kept = new List<Turn>();
            var chars = 0;
            foreach (var turn in Enumerable.Reverse(session.Turns))
            {
                chars += turn.User.Length + turn.Ekko.Length;
                if (chars > options.MaxHistoryChars)
                    break;
                kept.Insert(0, turn);
            }
            return new History(kept, session.Messages + 1 >= options.DeepAfter);
        }
    }

    public void Remember(string conversationId, string user, string ekko)
    {
        var session = _sessions.GetOrAdd(conversationId, _ => new Session());
        lock (session)
        {
            if (Ended(session))
            {
                session.Turns.Clear();
                session.Messages = 0;
            }
            session.Turns.Add(new(user, ekko.Length <= MaxReplyChars ? ekko : ekko[..MaxReplyChars] + "…"));
            if (session.Turns.Count > options.MaxTurns)
                session.Turns.RemoveAt(0);
            session.Messages++;
            session.Last = clock.GetUtcNow();
        }
    }

    private bool Ended(Session session) => clock.GetUtcNow() - session.Last > TimeSpan.FromMinutes(options.SessionMinutes);
}
