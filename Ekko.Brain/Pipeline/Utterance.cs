namespace Ekko.Brain.Pipeline;

/// <summary>Deterministic text handling, used before any model sees the message.</summary>
public static class Utterance
{
    // Words that never change what a short command means. "ok"/"okay" are absent on purpose: they're answers.
    private static readonly HashSet<string> Filler =
        ["hey", "ekko", "echo", "please", "the", "my", "can", "could", "would", "you", "just"];

    private static readonly HashSet<string> Yes =
        ["yes", "yeah", "yep", "yup", "sure", "ok", "okay", "do it", "go ahead", "confirm", "correct", "yes do it"];

    private static readonly HashSet<string> No =
        ["no", "nope", "cancel", "never mind", "nevermind", "don't", "no thanks", "stop", "forget it"];

    /// <summary>"Hey Ekko, can you mute my Discord, please?" -> "mute discord".</summary>
    public static string Simplify(string text)
    {
        var chars = text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '\'' ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Filler.Contains(w)));
    }

    public static bool IsYes(string text) => Yes.Contains(Simplify(text));

    public static bool IsNo(string text) => No.Contains(Simplify(text));
}
