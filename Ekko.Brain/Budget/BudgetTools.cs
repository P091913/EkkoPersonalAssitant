using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Budget;

public enum MonthName { January, February, March, April, May, June, July, August, September, October, November, December }

public sealed record BudgetStatusArgs(
    [property: Description("Only if the user named a month.")] MonthName? Month = null);

public sealed record MarkBillPaidArgs(
    [property: MinLength(1), MaxLength(80), Description("The bill or paycheck as the user named it, without words like paid or bill: 'water', 'Verizon', 'paycheck'.")] string Item,
    [property: Range(0.01, 100000), Description("What it really came to, in dollars, only if the user said an amount.")] decimal? Amount = null);

/// <param name="Amount">Only when it differs from the row's planned amount.</param>
public sealed record MarkRowPaidArgs(string Tab, int Row, string Item, string Label, decimal? Amount = null);

public sealed record AddExpenseArgs(
    [property: MinLength(1), MaxLength(80), Description("What it was, in a few words: 'new tire', 'lunch', 'refund'.")] string Item,
    [property: Range(0.01, 100000), Description("Dollars, positive.")] decimal Amount,
    [property: Description("True only when money came in: a refund, a deposit, something sold.")] bool Income = false,
    [property: Description("True only if the user said it was planned or expected. Otherwise it's marked unexpected.")] bool Planned = false);

[RiskClass(Risk.ReadOnly)]
public sealed class GetBudgetStatus(BudgetService budget)
    : Tool<BudgetStatusArgs>("get_budget_status", Domain.Budget,
        "Read a month of the expenses sheet: bills still unpaid, money still coming in, the lowest and ending balance, unexpected spending. " +
        "For 'what bills are left', 'how's the budget looking', 'how did October go'.")
{
    protected override async Task<ToolResult> RunAsync(BudgetStatusArgs args, CancellationToken ct) =>
        BudgetResult.From(await budget.GetAsync(args.Month is { } m ? $"budget/status?month={m}" : "budget/status", ct));
}

/// <summary>
/// Finds the row; it doesn't write. The write is <see cref="MarkRowPaid"/>, confirmed against the exact row the service
/// found ("Check off Water ($58 on Nov 15)?"), not against the model's guess at what the user meant.
/// </summary>
[RiskClass(Risk.ReadOnly)]
public sealed class MarkBillPaid(BudgetService budget)
    : Tool<MarkBillPaidArgs>("mark_bill_paid", Domain.Budget,
        "Check off a planned bill or paycheck on the expenses sheet when the user says it's paid or came in: " +
        "'I paid the water bill', 'Verizon is paid, it was 220', 'my paycheck came in'.")
{
    protected override async Task<ToolResult> RunAsync(MarkBillPaidArgs args, CancellationToken ct)
    {
        var answer = await budget.PostAsync("budget/bills/find", new { item = args.Item }, ct);
        var rows = answer.Candidates
            .Select(c => new MarkRowPaidArgs(c.Tab, c.Row, c.Item, c.Label, args.Amount == Math.Abs(c.Amount) ? null : args.Amount))
            .ToList();
        return answer.Status switch
        {
            "found" when rows is [var row] => ToolGate.Confirm(MarkRowPaid.ToolName, MarkRowPaid.Text(row), BudgetResult.Json(row)),
            "ambiguous" when rows.Count > 1 => ToolResult.Ask(answer.Message, new PendingChoice(MarkRowPaid.ToolName, rows.Select(Option).ToList())),
            _ => ToolResult.Failed(answer.Message),
        };
    }

    private static ChoiceOption Option(MarkRowPaidArgs row) =>
        new(row.Label, BudgetResult.Json(row), Utterance.Simplify(row.Item).Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Checks off the row the user confirmed. Code-only: the model never sees it, so it can't invent a row number.</summary>
[RiskClass(Risk.WriteConfirm)]
public sealed class MarkRowPaid(BudgetService budget)
    : Tool<MarkRowPaidArgs>(ToolName, Domain.Budget, "Check off one sheet row the user confirmed.")
{
    public const string ToolName = "mark_row_paid";

    public override bool ModelCanChoose => false;

    public static string Text(MarkRowPaidArgs a) => $"Check off {a.Label}" + (a.Amount is { } x ? $" at {BudgetResult.Money(x)} instead" : "");

    protected override string Describe(MarkRowPaidArgs args) => Text(args);

    protected override async Task<ToolResult> RunAsync(MarkRowPaidArgs args, CancellationToken ct) =>
        BudgetResult.From(await budget.PostAsync("budget/bills/paid", new { tab = args.Tab, row = args.Row, item = args.Item, amount = args.Amount }, ct));
}

[RiskClass(Risk.WriteConfirm)]
public sealed class AddExpense(BudgetService budget)
    : Tool<AddExpenseArgs>("add_expense", Domain.Budget,
        "Add something to the expenses sheet that isn't a planned row, dated today: 'unexpected expense, 380 for a new tire', " +
        "'I spent 12 bucks on lunch', 'I got 50 back from a refund'. For a bill already on the sheet, use mark_bill_paid.")
{
    protected override string Describe(AddExpenseArgs a) => a.Income
        ? $"Add {a.Item}, {BudgetResult.Money(a.Amount)} coming in"
        : $"Add {a.Item}, {BudgetResult.Money(a.Amount)} spent{(a.Planned ? "" : ", marked unexpected")}";

    protected override async Task<ToolResult> RunAsync(AddExpenseArgs args, CancellationToken ct) =>
        BudgetResult.From(await budget.PostAsync("budget/expenses", new { item = args.Item, amount = args.Amount, income = args.Income, planned = args.Planned }, ct));
}

internal static class BudgetResult
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    /// <summary>The budget service already words every outcome.</summary>
    public static ToolResult From(BudgetAnswer answer) =>
        answer.Status is "ok" or "already" ? ToolResult.Done(answer.Message) : ToolResult.Failed(answer.Message);

    public static string Money(decimal amount) => amount.ToString(amount == decimal.Truncate(amount) ? "C0" : "C2", En);

    public static JsonElement Json(MarkRowPaidArgs row) => JsonSerializer.SerializeToElement(row, ToolJson.Options);
}
