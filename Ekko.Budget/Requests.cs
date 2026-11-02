using System.Globalization;

namespace Ekko.Budget;

/// <summary>What a caller may send. Checked here, at the edge, because whatever passes ends up in the user's sheet.</summary>
public static class Requests
{
    private const decimal MaxAmount = 100_000m;

    public static string? MonthProblem(string? month) =>
        month is null || CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Contains(month.Trim(), StringComparer.OrdinalIgnoreCase)
            ? null
            : "month must be a month name, like November.";

    public static string? ItemProblem(string? item) =>
        string.IsNullOrWhiteSpace(item) ? "item is required."
        : item.Trim().Length > 80 ? "item is longer than 80 characters."
        : item.Any(char.IsControl) ? "item has control characters."
        : null;

    public static string? Problem(PaidRequest r) =>
        string.IsNullOrWhiteSpace(r.Tab) || r.Tab.Contains('\'') ? "tab is required."
        : r.Row is < 2 or > 300 ? "row must be from 2 to 300."
        : ItemProblem(r.Item) ?? AmountProblem(r.Amount, required: false);

    public static string? Problem(ExpenseRequest r) => ItemProblem(r.Item) ?? AmountProblem(r.Amount, required: true);

    private static string? AmountProblem(decimal? amount, bool required) =>
        amount is null ? (required ? "amount is required." : null)
        : amount is <= 0 or > MaxAmount ? $"amount must be more than 0 and at most {MaxAmount}."
        : decimal.Round(amount.Value, 2) != amount ? "amount can't go past cents."
        : null;
}
