using System.Text.Json;

namespace Ekko.Brain.Pipeline;

/// <param name="Keywords">Words that point at this option ("movie", "2002", title words), in <see cref="Utterance.Simplify"/> form.</param>
public sealed record ChoiceOption(string Label, JsonElement Args, IReadOnlyList<string> Keywords);

/// <summary>
/// Ekko offered options ("which one: A or B?"). The answer is matched in code - no model call - and the picked option
/// runs as <paramref name="Tool"/> with its Args, through the gate like any other call.
/// </summary>
public sealed record PendingChoice(string Tool, IReadOnlyList<ChoiceOption> Options) : Pending
{
    private static readonly string[][] Ordinals =
        [["first", "1st", "1"], ["second", "2nd", "2"], ["third", "3rd", "3"], ["fourth", "4th", "4"], ["fifth", "5th", "5"]];

    private static readonly HashSet<string> PositionWords = ["number", "option", "one"];

    /// <summary>The option the answer clearly picks, or null - the message is then handled as a new request.</summary>
    public ChoiceOption? Pick(string answer)
    {
        if (Options.Count == 1 && Utterance.IsYes(answer))
            return Options[0];

        var words = Utterance.Simplify(answer).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Only words that point at exactly one option count: "scooby" names them all. Checked before ordinals,
        // so a title word ("The Last of Us", "Scooby-Doo 2") or a date read out ("Nov 2") wins over "last" or "2" as a position.
        var scores = Options.Select(o => words.Count(w => o.Keywords.Contains(w) && Options.Count(p => p.Keywords.Contains(w)) == 1)).ToList();
        var best = scores.Max();
        if (best > 0 && scores.Count(s => s == best) == 1)
            return Options[scores.IndexOf(best)];

        if (words.Contains("last"))
            return Options[^1];
        // A bare digit is a position only when said on its own ("3", "number 3"); in "November 3" it's part of a date.
        var alone = words.All(w => w.All(char.IsAsciiDigit) || PositionWords.Contains(w));
        for (var i = 0; i < Math.Min(Ordinals.Length, Options.Count); i++)
            if (words.Any(w => Ordinals[i].Contains(w) && (alone || !w.All(char.IsAsciiDigit))))
                return Options[i];
        return null;
    }
}
