using System.Globalization;
using System.Text.RegularExpressions;

namespace Ekko.Food;

/// <summary>
/// How a food is matched and said. Matching is exact on the key, never fuzzy: a near miss becomes a new food the user
/// confirms, not a wrong one logged quietly. The old app's database search turned "big mac" into a Big Mac Salad (DR-35).
/// </summary>
public static partial class FoodWords
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private static readonly HashSet<string> Filler = ["a", "an", "the", "of", "some", "my", "with"];

    private static readonly Dictionary<string, string> Units = new()
    {
        ["g"] = "gram", ["oz"] = "ounce", ["lb"] = "pound", ["lbs"] = "pound", ["tbsp"] = "tablespoon", ["tsp"] = "teaspoon", ["ml"] = "milliliter",
    };

    /// <summary>"Eggs" and "egg", "cup of rice" and "rice cup", "Reese's" and "reeses": one food. Its words lowercased, singular, without filler, sorted.</summary>
    public static string Key(string food) => string.Join(' ',
        Word().Matches(food.ToLowerInvariant().Replace("'", "").Replace("’", "")).Select(m => m.Value)
            .Where(w => !Filler.Contains(w))
            .Select(w => Units.GetValueOrDefault(w) ?? Singular(w))
            .Order(StringComparer.Ordinal));

    // Only a key, never said: "cookie" and "cookies" both become "cooky", "pie" and "pies" both "py".
    private static string Singular(string w)
    {
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("us") && !w.EndsWith("is"))
            w = w.EndsWith("ies") ? w[..^3] + "y"
                : w.EndsWith("oes") || w.EndsWith("ches") || w.EndsWith("shes") || w.EndsWith("sses") || w.EndsWith("xes") ? w[..^2]
                : w[..^1];
        return w.EndsWith("ie") ? w[..^2] + "y" : w;
    }

    /// <summary>"2 eggs", "2 slices of pizza", "half a sandwich", "big mac".</summary>
    public static string Amount(string name, double quantity) => quantity switch
    {
        1 => name,
        0.5 => $"half a {name}",
        _ => $"{quantity.ToString("0.##", En)} {Plural(name)}",
    };

    private static string Plural(string name)
    {
        // The measure is what's counted: "slices of pizza", "grams of chicken".
        var of = name.IndexOf(" of ", StringComparison.Ordinal);
        var end = of < 0 ? name.Length : of;
        var start = name.LastIndexOf(' ', end - 1) + 1;
        var head = name[start..end];
        var plural = head.EndsWith('s') ? head
            : head.EndsWith("ch") || head.EndsWith("sh") || head.EndsWith('x') ? head + "es"
            : head.Length > 1 && head.EndsWith('y') && !"aeiou".Contains(head[^2]) ? head[..^1] + "ies"
            : head + "s";
        return name[..start] + plural + name[end..];
    }

    /// <summary>"1,450 calories"; small measures keep their decimals ("1.65 calories" a gram).</summary>
    public static string Calories(double calories) =>
        (calories < 10 ? calories.ToString("0.##", En) : Math.Round(calories).ToString("N0", En)) + (calories == 1 ? " calorie" : " calories");

    public static string Join(IReadOnlyList<string> parts) =>
        parts.Count <= 1 ? string.Concat(parts) : $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}";

    // Letters, digits, and the % of "2% milk" and & of "a&w": they tell foods apart.
    [GeneratedRegex("[a-z0-9%&]+")]
    private static partial Regex Word();
}
