namespace Ekko.Food;

/// <summary>What a caller may send. Checked here, at the edge, because whatever passes is kept in the diary.</summary>
public static class Requests
{
    public const int MaxItems = 10;
    // Grams count one by one: "200 grams of chicken" is 200 of "gram of chicken".
    private const double MaxQuantity = 2000;
    private const double MaxCaloriesEach = 3000;

    public static string? Problem(LogRequest r) =>
        r.Items is not { Count: > 0 } ? "items is required."
        : r.Items.Count > MaxItems ? $"items can have at most {MaxItems} foods."
        : r.Items.Select(ItemProblem).FirstOrDefault(p => p is not null);

    public static string? Problem(FoodRequest r) => FoodProblem(r.Food) ?? CaloriesProblem(r.CaloriesEach, required: true);

    private static string? ItemProblem(LogItem? i) =>
        i is null ? "an item is empty."
        : FoodProblem(i.Food)
          ?? (i.Quantity is > 0 and <= MaxQuantity ? null : $"quantity must be more than 0 and at most {MaxQuantity}.")
          ?? CaloriesProblem(i.CaloriesEach, required: i.Save);

    public static string? FoodProblem(string? food) =>
        string.IsNullOrWhiteSpace(food) ? "food is required."
        : food.Trim().Length > 60 ? "food is longer than 60 characters."
        : food.Any(char.IsControl) ? "food has control characters."
        : FoodWords.Key(food).Length == 0 ? "food has no words to match."
        : null;

    private static string? CaloriesProblem(double? calories, bool required) =>
        calories is null ? (required ? "caloriesEach is required." : null)
        : calories is >= 0 and <= MaxCaloriesEach ? null
        : $"caloriesEach must be from 0 to {MaxCaloriesEach}.";
}
