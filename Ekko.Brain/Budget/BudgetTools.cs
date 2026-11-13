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

public enum BillsWhen { Yesterday, Today, Tomorrow, Week }

public sealed record BillsArgs(
    [property: Description("week is the next 7 days from today: 'this week', 'coming up', 'the next few days'.")] BillsWhen When = BillsWhen.Week);

public sealed record MarkBillPaidArgs(
    [property: MinLength(1), MaxLength(80), Description("The row as the user named it, without words like paid, bill or expense: 'water', 'Verizon', 'paycheck', 'Netflix'. Leave it out if they gave only an amount.")] string? Item = null,
    [property: Range(0.01, 100000), Description("Dollars, only if the user said an amount: what it really came to, or the amount that picks out the row ('the 12.47 one').")] decimal? Amount = null);

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
        "For 'what bills are left', 'how's the budget looking', 'how did October go', 'did my paycheck come in yet'.")
{
    protected override async Task<ToolResult> RunAsync(BudgetStatusArgs args, CancellationToken ct) =>
        BudgetResult.From(await budget.GetAsync(args.Month is { } m ? $"budget/status?month={m}" : "budget/status", ct));
}

/// <summary>A day's bills, or the next 7 days': the service picks the rows by date and words the answer (DR-50).</summary>
[RiskClass(Risk.ReadOnly)]
public sealed class GetBills(BudgetService budget)
    : Tool<BillsArgs>("get_bills", Domain.Budget,
        "List what goes out on one day, or in the next 7 days, from the expenses sheet: 'what bills came out yesterday', " +
        "'what bills come out tomorrow', 'anything due today', 'what bills are coming out this week'.")
{
    protected override async Task<ToolResult> RunAsync(BillsArgs args, CancellationToken ct) =>
        BudgetResult.From(await budget.GetAsync($"budget/bills?when={args.When.ToString().ToLowerInvariant()}", ct));
}

/// <summary>The sheet's own balances: now, just before the next payday, and at the month's end (DR-50).</summary>
[RiskClass(Risk.ReadOnly)]
public sealed class GetMoneyLeft(BudgetService budget)
    : Tool<NoArgs>("get_money_left", Domain.Budget,
        "How much money there is now, how much will be left just before the next payday, and how the month ends: " +
        "'how much will we have left', 'what budget do we have for the rest of the month', 'how much money do we have', " +
        "'will we make it to payday', 'are we on track with our budget'.")
{
    protected override async Task<ToolResult> RunAsync(NoArgs args, CancellationToken ct) =>
        BudgetResult.From(await budget.GetAsync("budget/left", ct));
}

/// <summary>
/// Finds the row; it doesn't write. The write is <see cref="MarkRowPaid"/>, confirmed against the exact row the service
/// found ("Check off Water ($58 on Nov 15)?"), not against the model's guess at what the user meant.
/// </summary>
[RiskClass(Risk.ReadOnly)]
public sealed class MarkBillPaid(BudgetService budget)
    : Tool<MarkBillPaidArgs>("mark_bill_paid", Domain.Budget,
        "Check off a row already on the expenses sheet (a bill, subscription, paycheck or planned expense) when the user says it's " +
        "paid, came in or went through, or asks to mark it, check it off or mark it complete: 'I paid the water bill', " +
        "'Verizon is paid, it was 220', 'my paycheck came in', 'mark Netflix as paid', 'check off the 12.47 one'.")
{
    protected override async Task<ToolResult> RunAsync(MarkBillPaidArgs args, CancellationToken ct)
    {
        if (args.Item is null && args.Amount is null)
            return ToolResult.Failed("Which one? Say its name or its amount.");
        // The service finds by name when the item names something, otherwise by amount (Ledger.FindUnchecked).
        var answer = await budget.PostAsync("budget/bills/find", new { item = args.Item, amount = args.Amount }, ct);
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

    private static ChoiceOption Option(MarkRowPaidArgs row) => new(row.Label, BudgetResult.Json(row), Keywords(row.Label));

    /// <summary>
    /// An option matches what Ekko read out: every word of its label ("streaming basic 12.47 on nov 2"), plus
    /// the date as people say it ("november", "2nd"). So "November 2nd" or "12.47" picks the row. Words every option shares
    /// ("streaming", "nov") don't count (PendingChoice.Pick).
    /// </summary>
    internal static IReadOnlyList<string> Keywords(string label)
    {
        var words = Utterance.Simplify(label).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var said = new List<string>();
        for (var i = 1; i < words.Count; i++)
            if (Months.TryGetValue(words[i - 1], out var month) && int.TryParse(words[i], out var day))
                said.AddRange([month, Ordinal(day)]);
        return [.. words, .. said];
    }

    private static readonly Dictionary<string, string> Months = Enumerable.Range(0, 12).ToDictionary(
        m => BudgetResult.En.DateTimeFormat.AbbreviatedMonthNames[m].ToLowerInvariant(),
        m => BudgetResult.En.DateTimeFormat.MonthNames[m].ToLowerInvariant());

    private static string Ordinal(int day) =>
        day + (day % 100 is 11 or 12 or 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
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
        "Add a new row for one-off money spent or received, dated today: 'unexpected expense, 380 for a new tire', " +
        "'I spent 12 bucks on lunch', 'I got 50 back from a refund'. Recurring bills, insurance, subscriptions and paychecks are " +
        "already rows on the sheet: one of those being paid, going through or checked off is mark_bill_paid, never this tool.")
{
    protected override string Describe(AddExpenseArgs a) => a.Income
        ? $"Add {a.Item}, {BudgetResult.Money(a.Amount)} coming in"
        : $"Add {a.Item}, {BudgetResult.Money(a.Amount)} spent{(a.Planned ? "" : ", marked unexpected")}";

    protected override async Task<ToolResult> RunAsync(AddExpenseArgs args, CancellationToken ct) =>
        BudgetResult.From(await budget.PostAsync("budget/expenses", new { item = args.Item, amount = args.Amount, income = args.Income, planned = args.Planned }, ct));
}

internal static class BudgetResult
{
    public static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    /// <summary>The budget service already words every outcome.</summary>
    public static ToolResult From(BudgetAnswer answer) =>
        answer.Status is "ok" or "already" ? ToolResult.Done(answer.Message) : ToolResult.Failed(answer.Message);

    public static string Money(decimal amount) => amount.ToString(amount == decimal.Truncate(amount) ? "C0" : "C2", En);

    public static JsonElement Json(MarkRowPaidArgs row) => JsonSerializer.SerializeToElement(row, ToolJson.Options);
}
