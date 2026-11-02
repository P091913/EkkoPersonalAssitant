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

    // Answers to a waiting server-ops run. "stop" is left out on purpose: on its own it means the TV.
    private static readonly HashSet<string> Approve = ["approve", "approved", "approve it", "i approve"];
    private static readonly HashSet<string> Deny = ["deny", "denied", "deny it", "no", "nope", "don't", "cancel", "never mind", "nevermind", "no thanks", "forget it", "leave it"];

    public static bool IsApprove(string text) => IsYes(text) || Approve.Contains(Simplify(text));

    public static bool IsDeny(string text) => Deny.Contains(Simplify(text));
}
