using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ekko.Brain.Llm;
using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Food;

public sealed record FoodItem(
    [property: MinLength(1), MaxLength(60), Description("What was eaten or drunk, singular, with its measure if one was said: 'egg', 'slice of pizza', 'cup of rice', 'gram of chicken', 'big mac'. Never how much ('two', 'half a', '200'): that is quantity.")] string Food,
    [property: Range(0.01, 2000), Description("How many of that: 2 eggs is 2, 200 grams is 200, half a sandwich is 0.5.")] double Quantity = 1,
    [property: Range(0, 10000), Description("Only if the user said a calorie number for this food: that number.")] double? Calories = null);

public sealed record LogFoodArgs(
    [property: MinLength(1), MaxLength(10), Description("One item per food.")] IReadOnlyList<FoodItem> Items) : IValidatableObject
{
    // DataAnnotations stop at the list: each item's own lengths and ranges are checked here.
    public IEnumerable<ValidationResult> Validate(ValidationContext context) => Items.SelectMany(item =>
    {
        if (item is null)
            return [new ValidationResult("an item is null")];
        var problems = new List<ValidationResult>();
        Validator.TryValidateObject(item, new ValidationContext(item), problems, validateAllProperties: true);
        return problems;
    });
}

/// <param name="CaloriesEach">For one of it; null means the food service uses its list.</param>
/// <param name="Save">Put the food on the list: only in a <see cref="LogNewFood"/> the user said yes to.</param>
public sealed record FoodEntry(string Food, double Quantity, double? CaloriesEach = null, bool Save = false);

public sealed record LogNewFoodArgs(IReadOnlyList<FoodEntry> Items);

public enum FoodDay { Today, Yesterday }

public sealed record FoodLogArgs([property: Description("Only if the user asked about yesterday.")] FoodDay Day = FoodDay.Today);

public sealed record SetFoodCaloriesArgs(
    [property: MinLength(1), MaxLength(60), Description("The food as the user named it, singular, with its measure if said.")] string Food,
    [property: Range(0, 3000), Description("Calories in one of it.")] double Calories);

/// <summary>
/// Logs foods by name: the food service matches them to the user's list and does the math. A food not on the list gets
/// one estimate and a read-back, and is saved only on yes (DR-35), so no number reaches the diary that nobody checked.
/// </summary>
[RiskClass(Risk.Reversible)]
public sealed partial class LogFood(FoodService food, CalorieEstimator estimator)
    : Tool<LogFoodArgs>("log_food", Domain.Food,
        "Log what the user ate or drank: 'I had two eggs and toast', 'log a big mac', 'I ate 200 grams of chicken', " +
        "'I had a 300 calorie protein bar'. Also a different number after Ekko asked \"New food: ... Go ahead?\" ('more like 560', " +
        "'it's 450'): that food again, with the number as its calories.")
{
    protected override async Task<ToolResult> RunAsync(LogFoodArgs args, CancellationToken ct)
    {
        var items = args.Items.Select(Entry).ToList();
        var answer = await food.PostAsync("food/log", new { items }, ct);
        if (answer.Status != "unknown")
            return FoodResult.From(answer);

        for (var n = 0; n < items.Count; n++)
        {
            if (!answer.Unknown.Contains(items[n].Food))
                continue;
            if ((items[n].CaloriesEach ?? await estimator.EstimateAsync(items[n].Food, ct)) is not { } each)
                return ToolResult.Failed($"I couldn't estimate {items[n].Food}. How many calories was it?");
            items[n] = items[n] with { CaloriesEach = each, Save = true };
        }
        var confirm = new LogNewFoodArgs(items);
        return ToolGate.Confirm(LogNewFood.ToolName, LogNewFood.Text(confirm), JsonSerializer.SerializeToElement(confirm, ToolJson.Options));
    }

    /// <summary>
    /// Qwen sometimes leaves "half a" in the food, with or without quantity 0.5 (evals, Nov 3): it's the quantity, or
    /// "half a turkey sandwich" becomes a food of its own. A number the user said is for the food as eaten; the list keeps calories for one.
    /// </summary>
    public static FoodEntry Entry(FoodItem item)
    {
        var (food, quantity) = (item.Food.Trim(), item.Quantity);
        if (Half().Match(food) is { Success: true } half && half.Length < food.Length)
            (food, quantity) = (food[half.Length..], quantity == 1 ? 0.5 : quantity);
        return new FoodEntry(food, quantity, item.Calories / quantity);
    }

    // Not "half and half", the creamer.
    [GeneratedRegex(@"^half\s+(?!and\b)(?:of\s+)?(?:an?\s+)?", RegexOptions.IgnoreCase)]
    private static partial Regex Half();
}

/// <summary>Saves the new foods and logs the message, with exactly the numbers that were read back. Code-only.</summary>
[RiskClass(Risk.WriteConfirm)]
public sealed class LogNewFood(FoodService food) : Tool<LogNewFoodArgs>(ToolName, Domain.Food, "Save new foods and log them, as confirmed.")
{
    public const string ToolName = "log_new_food";

    public override bool ModelCanChoose => false;

    /// <summary>"New food: big mac at 590 calories each. Log big mac and 2 eggs".</summary>
    public static string Text(LogNewFoodArgs a)
    {
        var news = a.Items.Where(i => i.Save).Select(i => $"{i.Food} at {FoodWords.Calories(i.CaloriesEach ?? 0)} each").ToList();
        return $"New food{(news.Count > 1 ? "s" : "")}: {FoodWords.Join(news)}. Log {FoodWords.Join(a.Items.Select(i => FoodWords.Amount(i.Food, i.Quantity)).ToList())}";
    }

    protected override string Describe(LogNewFoodArgs args) => Text(args);

    protected override async Task<ToolResult> RunAsync(LogNewFoodArgs args, CancellationToken ct) =>
        FoodResult.From(await food.PostAsync("food/log", new { items = args.Items }, ct));
}

[RiskClass(Risk.ReadOnly)]
public sealed class GetFoodLog(FoodService food)
    : Tool<FoodLogArgs>("get_food_log", Domain.Food,
        "Read the food diary for today or yesterday: calories so far and what was eaten. " +
        "'How many calories have I had', 'what did I eat yesterday'.")
{
    protected override async Task<ToolResult> RunAsync(FoodLogArgs args, CancellationToken ct) =>
        FoodResult.From(await food.GetAsync(args.Day == FoodDay.Yesterday ? "food/day?day=yesterday" : "food/day?day=today", ct));
}

/// <summary>Reversible like logging: what it takes off can be said again.</summary>
[RiskClass(Risk.Reversible)]
public sealed class UndoFood(FoodService food)
    : Tool<NoArgs>("undo_food", Domain.Food, "Take back the last food logged today: 'undo that', 'I didn't actually eat that', 'remove the last thing I logged'.")
{
    protected override async Task<ToolResult> RunAsync(NoArgs args, CancellationToken ct) =>
        FoodResult.From(await food.PostAsync("food/undo", null, ct));
}

/// <summary>
/// Finds the food first, so the read-back says what changes (DR-25): "Change big mac from 590 to 560 calories each".
/// A food that isn't on the list has nothing to change. That's nearly always a correction to a "New food: ... Go ahead?"
/// the model took for a change ("more like 560"), so it gets the same new-food read-back as logging: a yes never saves
/// a number without logging the food.
/// </summary>
[RiskClass(Risk.ReadOnly)]
public sealed class SetFoodCalories(FoodService food)
    : Tool<SetFoodCaloriesArgs>("set_food_calories", Domain.Food,
        "Change the calories of a food already saved on the user's list, when they aren't eating it now: 'toast is 90 calories', " +
        "'change my protein shake to 250 calories'. Never the answer to Ekko's \"New food: ... Go ahead?\": a number then corrects that food, which is log_food.")
{
    protected override async Task<ToolResult> RunAsync(SetFoodCaloriesArgs args, CancellationToken ct)
    {
        var said = args.Food.Trim();
        var answer = await food.GetAsync($"food/foods?food={Uri.EscapeDataString(said)}", ct);
        if (answer is { Status: "found", Name: { } name, CaloriesEach: { } was })
        {
            var change = new SaveFoodCaloriesArgs(name, args.Calories);
            return ToolGate.Confirm(SaveFoodCalories.ToolName, $"Change {name} from {FoodWords.Calories(was)} to {FoodWords.Calories(args.Calories)} each",
                JsonSerializer.SerializeToElement(change, ToolJson.Options));
        }
        if (answer.Status != "unknown")
            return FoodResult.From(answer);
        var log = new LogNewFoodArgs([new FoodEntry(said, 1, args.Calories, Save: true)]);
        return ToolGate.Confirm(LogNewFood.ToolName, LogNewFood.Text(log), JsonSerializer.SerializeToElement(log, ToolJson.Options));
    }
}

public sealed record SaveFoodCaloriesArgs(string Food, double Calories);

/// <summary>Saves a food's new calories once the user said yes to the change. Code-only.</summary>
[RiskClass(Risk.WriteConfirm)]
public sealed class SaveFoodCalories(FoodService food) : Tool<SaveFoodCaloriesArgs>(ToolName, Domain.Food, "Save a food's calories, as confirmed.")
{
    public const string ToolName = "save_food_calories";

    public override bool ModelCanChoose => false;

    protected override string Describe(SaveFoodCaloriesArgs a) => $"Change {a.Food} to {FoodWords.Calories(a.Calories)} each";

    protected override async Task<ToolResult> RunAsync(SaveFoodCaloriesArgs args, CancellationToken ct) =>
        FoodResult.From(await food.PostAsync("food/foods", new { food = args.Food, caloriesEach = args.Calories }, ct));
}

public sealed record CalorieEstimate([property: Range(0, 3000)] double Calories);

/// <summary>
/// One model call per food that isn't on the list yet, on Claude (Llm:Steps:estimate). The model only proposes the number;
/// the user hears it and says yes before it's saved, and after that it's never estimated again (DR-35).
/// </summary>
public sealed class CalorieEstimator(StructuredCall llm, CurrentTurn turn)
{
    private static readonly JsonNode Schema = ToolJson.SchemaFor<CalorieEstimate>();

    private const string Prompt = """
        You estimate calories for a food diary. The message names one food, with its measure if the user said one:
        "slice of pizza", "gram of chicken", "big mac", "cup of rice".
        Answer the calories in ONE of it: one slice, one gram, one burger, one cup.
        For a restaurant or packaged item, use its published figure. Otherwise assume a typical US portion.
        Answer {"calories": <number>}.
        """;

    public async Task<double?> EstimateAsync(string food, CancellationToken ct) =>
        (await llm.AskAsync("estimate", new LlmRequest(Prompt, [new("user", food)], Schema), StructuredCall.Parse<CalorieEstimate>(), turn.Trace, ct))
            ?.Calories;
}

internal static class FoodResult
{
    /// <summary>The food service already words every outcome.</summary>
    public static ToolResult From(FoodAnswer answer) =>
        answer.Status is "ok" or "empty" ? ToolResult.Done(answer.Message) : ToolResult.Failed(answer.Message);
}

/// <summary>How a read-back says foods. The same words as Ekko.Food's, so the confirm and the logged reply agree.</summary>
internal static class FoodWords
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    public static string Amount(string name, double quantity) => quantity switch
    {
        1 => name,
        0.5 => $"half a {name}",
        _ => $"{quantity.ToString("0.##", En)} {Plural(name)}",
    };

    private static string Plural(string name)
    {
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

    public static string Calories(double calories) =>
        (calories < 10 ? calories.ToString("0.##", En) : Math.Round(calories).ToString("N0", En)) + (calories == 1 ? " calorie" : " calories");

    public static string Join(IReadOnlyList<string> parts) =>
        parts.Count <= 1 ? string.Concat(parts) : $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}";
}
