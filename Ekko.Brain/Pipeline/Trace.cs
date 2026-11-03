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
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // a log file, never HTML: keep it readable
    };

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    public TraceLog(string path, TimeProvider clock)
    {
        _path = Path.GetFullPath(path);
        _clock = clock;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public void Write(TraceEvent e)
    {
        var line = JsonSerializer.Serialize(e with { Ts = _clock.GetUtcNow() }, Json);
        lock (_gate)
            File.AppendAllText(_path, line + "\n");
    }
}
