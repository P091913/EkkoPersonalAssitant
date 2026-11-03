using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ekko.Brain.Pipeline;

/// <summary>
/// One trace line. A turn creates one with its ids, then writes a copy per step (<c>t with { Step = ... }</c>),
/// so every line of a turn shares request_id and only the step's own fields are set.
/// </summary>
public sealed record TraceEvent(string RequestId, string ConversationId, string Channel)
{
    [JsonPropertyOrder(-1)] public DateTimeOffset Ts { get; init; }
    public string Step { get; init; } = "";
    public bool? Hit { get; init; }
    public string? Domain { get; init; }
    public string? Tool { get; init; }
    public JsonElement? Args { get; init; }
    /// <summary>User text on the request line; the prompt on a model line.</summary>
    public string? Input { get; init; }
    /// <summary>Raw model output, before validation.</summary>
    public string? Output { get; init; }
    /// <summary>Tool result or reply text.</summary>
    public string? Result { get; init; }
    public bool? Ok { get; init; }
    public string? Model { get; init; }
    /// <summary>On model lines: 1 = first try, 2 = retry with the validation error, 3 = escalated to the fallback model.</summary>
    public int? Attempt { get; init; }
    public long? LatencyMs { get; init; }
    public int? TokensIn { get; init; }
    public int? TokensOut { get; init; }
    /// <summary>On the request line: earlier turns the message was handled with (DR-33).</summary>
    public int? Turns { get; init; }
    /// <summary>On the request line and model lines: the session was deep, so steps used their Deep model (DR-34).</summary>
    public bool? Deep { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// The turn this request scope is serving, set by <see cref="MessagePipeline"/>. A tool that asks a model (a food estimate)
/// gets no <see cref="TraceEvent"/> from the gate, so it takes this one, and its model lines share the turn's request_id.
/// </summary>
public sealed class CurrentTurn
{
    public TraceEvent Trace { get; set; } = new("none", "none", "none");
}

/// <summary>Appends one JSON line per step. Never write credentials into an event: this file is meant to be read and mined for evals.</summary>
public sealed class TraceLog
{
    /// <summary>How many recent steps <see cref="Since"/> can return.</summary>
    public const int Keep = 1000;

    private const int TailBytes = 4 << 20; // model lines carry whole prompts, so 1000 steps can be a few MB

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // a log file, never HTML: keep it readable
    };

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Queue<TraceView> _recent = new();
    private long _seq;

    public TraceLog(string path, TimeProvider clock)
    {
        _path = Path.GetFullPath(path);
        _clock = clock;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Seed();
    }

    /// <summary>New on every start, when sequence numbers begin again: a reader that sees it change starts over.</summary>
    public string Boot { get; } = Guid.NewGuid().ToString("N")[..8];

    public void Write(TraceEvent e)
    {
        var stamped = e with { Ts = _clock.GetUtcNow() };
        var line = JsonSerializer.Serialize(stamped, Json);
        lock (_gate)
        {
            File.AppendAllText(_path, line + "\n");
            Remember(stamped);
        }
    }

    /// <summary>Kept steps with a sequence number above <paramref name="after"/>, oldest first.</summary>
    public TraceView[] Since(long after)
    {
        lock (_gate)
            return _recent.Where(v => v.Seq > after).ToArray();
    }

    private void Remember(TraceEvent e)
    {
        _recent.Enqueue(TraceView.Of(++_seq, e));
        if (_recent.Count > Keep)
            _recent.Dequeue();
    }

    /// <summary>The newest steps already in the file, so a live view isn't empty after a restart. Only the tail is read: the file only grows.</summary>
    private void Seed()
    {
        if (!File.Exists(_path))
            return;
        using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var start = Math.Max(0, file.Length - TailBytes);
        file.Seek(start, SeekOrigin.Begin);
        var lines = new StreamReader(file).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(start > 0 ? 1 : 0)) // reading from the middle starts partway through a line
        {
            try
            {
                if (JsonSerializer.Deserialize<TraceEvent>(line, Json) is { } e)
                    Remember(e);
            }
            catch (JsonException)
            {
                // a line cut short by a crash: skip it
            }
        }
    }
}

/// <summary>
/// What GET /trace shows of a step, for live views like the deck: what happened and how it went. Prompts, raw model
/// output and tool arguments stay in the file.
/// </summary>
public sealed record TraceView(long Seq, DateTimeOffset Ts, string RequestId, string Channel, string Step)
{
    public string? Domain { get; init; }
    public string? Tool { get; init; }
    public string? Model { get; init; }
    public bool? Ok { get; init; }
    public bool? Hit { get; init; }
    public long? LatencyMs { get; init; }
    public int? TokensIn { get; init; }
    public int? TokensOut { get; init; }

    /// <summary>The user's words on a request line, otherwise the line's result (what was said, a run's state). One line, cut short.</summary>
    public string? Text { get; init; }

    public string? Error { get; init; }

    public static TraceView Of(long seq, TraceEvent e) => new(seq, e.Ts, e.RequestId, e.Channel, e.Step)
    {
        Domain = e.Domain, Tool = e.Tool, Model = e.Model, Ok = e.Ok, Hit = e.Hit,
        LatencyMs = e.LatencyMs, TokensIn = e.TokensIn, TokensOut = e.TokensOut,
        // A model line's input is the prompt, and its result isn't for people.
        Text = Short(e.Step == "request" ? e.Input : e.Step.StartsWith("llm:") ? null : e.Result, 160),
        Error = Short(e.Error, 200), // an exception's first line, not its stack
    };

    private static string? Short(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        var line = s.Trim().Split('\n', 2)[0].TrimEnd('\r');
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }
}
