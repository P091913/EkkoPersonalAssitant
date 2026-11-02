using System.Text.Json;

namespace Ekko.Brain.Pipeline;

public sealed record FastMatch(string Tool, JsonElement Args);

/// <summary>
/// Exact phrases, compared after <see cref="Utterance.Simplify"/>, mapped straight to a tool call. No model, so it's
/// instant and can't misfire; anything not in the table goes to the router. A match still goes through the risk gate.
/// </summary>
public static class FastPath
{
    // Rule: a bare "pause"/"resume"/"stop" means the TV; the PC needs a qualifier ("pause music").
    private static readonly Dictionary<string, FastMatch> Commands = Table(
        (["pause", "pause tv", "pause show", "pause movie"], "media_control", """{"action":"pause"}"""),
        (["resume", "unpause", "play", "resume tv", "keep playing"], "media_control", """{"action":"resume"}"""),
        (["stop", "stop tv", "stop playing", "stop show", "stop movie"], "media_control", """{"action":"stop"}"""),
        (["mute discord", "unmute discord", "discord mute", "discord unmute", "toggle discord mute"], "discord_toggle_mute", "{}"),
        (["deafen discord", "undeafen discord", "discord deafen", "deafen", "undeafen"], "discord_toggle_deafen", "{}"),
        (["volume up", "turn it up", "turn volume up", "turn up volume", "louder"], "adjust_volume", """{"direction":"up"}"""),
        (["volume down", "turn it down", "turn volume down", "turn down volume", "quieter"], "adjust_volume", """{"direction":"down"}"""),
        (["open steam", "launch steam", "start steam"], "open_app", """{"app":"steam"}"""),
        (["lock pc", "lock computer", "lock screen"], "lock_pc", "{}"),
        (["pause music", "resume music", "pause pc", "play pause"], "media_play_pause", "{}"));

    public static IEnumerable<string> Phrases => Commands.Keys;

    public static FastMatch? Match(string text) => Commands.GetValueOrDefault(Utterance.Simplify(text));

    private static Dictionary<string, FastMatch> Table(params (string[] Phrases, string Tool, string Args)[] rows) =>
        rows.SelectMany(r => r.Phrases.Select(p => (p, new FastMatch(r.Tool, JsonDocument.Parse(r.Args).RootElement.Clone()))))
            .ToDictionary(x => x.p, x => x.Item2);
}
