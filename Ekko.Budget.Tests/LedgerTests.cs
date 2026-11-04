using System.Text.Json.Nodes;
using Ekko.Budget;

namespace Ekko.Budget.Tests;

public class LedgerTests
{
    private static readonly Layout Columns = new(Date: 0, Item: 1, Amount: 2, Balance: 3, Complete: 4, Unexpected: null);
    private static readonly DateOnly Nov16 = new(2026, 11, 16);

    private static Row R(int n, string? date, string item, decimal amount, decimal balance, bool done, string? formula = "chain") =>
        new(n, date is null ? null : DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), item, amount, balance, done, false,
            formula == "chain" ? $"=D{n - 1}+C{n}" : formula);

    /// <summary>A November tab shaped like the real one: starting balance, planned bills and a paycheck, some checked off.</summary>
    private static Month November(Layout? layout = null) => new("November", 42, layout ?? Columns,
    [
        R(2, "2026-10-31", "Starting balance", 1000, 1000, false, formula: null),
        R(3, "2026-11-01", "Rent", -800, 200, true),
        R(4, "2026-11-05", "Paycheck", 1500, 1700, false),
        R(5, "2026-11-07", "Groceries - shop 1", -150, 1550, true),
        R(6, "2026-11-14", "Groceries - shop 2", -150, 1400, false),
        R(7, "2026-11-15", "Water", -58, 1342, false),
        R(8, "2026-11-20", "Water softener", -40, 1302, false),
        R(9, "2026-11-21", "Groceries - shop 3", -150, 1152, false),
        R(10, "2026-11-28", "Car insurance", -190, 962, false),
        R(11, "2026-11-28", "Sewage insurance", -40, 922, false),
    ]);

    private static List<int> Found(string said, decimal? amount = null) =>
        Ledger.FindUnchecked(November(), said, Nov16, amount).Select(r => r.Number).ToList();

    [Fact]
    public void The_layout_comes_from_the_header_whatever_the_checkbox_column_is_called()
    {
        JsonNode?[] header = ["Date", "Item", "Amount", "Balance", "SpentSoFar", "Unexpected"];
        Assert.Equal(new Layout(0, 1, 2, 3, 4, 5), Ledger.FindLayout(header));
        Assert.Equal(4, Ledger.FindLayout(["Date", "Description", "Amount", "Balance", "Whatever"])!.Complete); // the one after Balance
        Assert.Null(Ledger.FindLayout(["Date", "Item", "Balance"]));
    }

    [Fact]
    public void Rows_are_read_down_to_the_first_without_an_item()
    {
        List<List<JsonNode?>> values =
        [
            ["Date", "Item", "Amount", "Balance", "Complete"],
            ["11/1/2026", "Rent", -800, "$1,240.50", true],
            ["", "Unscheduled", "", 1240.5, "FALSE"],
            [null, ""],
            ["11/9/2026", "Below the gap", -1, 0, false],
        ];
        List<List<JsonNode?>> formulas = [[], [null, null, null, "=D1+C2"], [null, null, null, "1240.5"]];

        var rows = Ledger.ReadRows(values, formulas, Columns);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new Row(2, new DateOnly(2026, 11, 1), "Rent", -800, 1240.50m, true, false, "=D1+C2"), rows[0]);
        Assert.Equal(new Row(3, null, "Unscheduled", null, 1240.5m, false, false, null), rows[1]);
    }

    [Fact]
    public void A_glance_counts_unchecked_bills_and_names_the_soonest()
    {
        var glance = Ledger.Glance(November());
        Assert.Equal(new Glance("ok", "6 bills left, $628 in all. Next: Groceries - shop 2, $150 on Nov 14. Ending balance $922.",
            6, 628, 922, new NextBill("Groceries - shop 2", 150, new DateOnly(2026, 11, 14))), glance);

        var month = November();
        month.Rows[1] = month.Rows[1] with { Complete = false }; // rent, Nov 1, not paid yet
        Assert.Equal("Rent", Ledger.Glance(month).Next!.Item); // overdue comes first

        month.Rows.RemoveAll(r => r.Amount < 0 && !r.IsStart);
        Assert.Equal((0, 0m, null), (Ledger.Glance(month).BillsLeft, Ledger.Glance(month).LeftToPay, Ledger.Glance(month).Next));
    }

    [Fact]
    public void A_row_named_exactly_as_said_wins_over_longer_names() => Assert.Equal([7], Found("the water bill"));

    [Fact]
    public void Different_rows_that_fit_are_all_returned_to_ask_about() => Assert.Equal([10, 11], Found("insurance"));

    [Fact]
    public void Rows_named_alike_but_numbered_mean_the_unchecked_one_nearest_today() => Assert.Equal([6], Found("groceries"));

    [Theory]
    [InlineData("rent")]       // already checked off
    [InlineData("it")]         // nothing that names a row
    [InlineData("electric")]   // no such row
    public void Nothing_fits(string said) => Assert.Empty(Found(said));

    [Fact]
    public void Words_match_the_start_of_a_rows_words() => Assert.Equal([10], Found("car"));

    [Theory]
    [InlineData("", 58, new[] { 7 })]                 // "check off the $58 one"
    [InlineData("expense", 58, new[] { 7 })]          // "mark an expense off my spreadsheet, $58": no name in that
    [InlineData("the 58 one", 58, new[] { 7 })]       // the number is the amount, not a name
    [InlineData("", 40, new[] { 8, 11 })]             // two rows at $40: ask
    [InlineData("", 150, new[] { 6 })]                // the numbered Groceries rows: the one nearest today
    [InlineData("", 800, new int[0])]                 // Rent is $800 but already checked off
    [InlineData("", 99, new int[0])]
    public void Without_a_name_the_amount_finds_the_row(string said, decimal amount, int[] rows) => Assert.Equal(rows, Found(said, amount));

    [Fact]
    public void With_a_name_the_amount_is_what_it_came_to_not_part_of_the_lookup() => Assert.Equal([7], Found("water", 60));

    [Theory]
    [InlineData("water", true)]
    [InlineData("the expense", false)]
    [InlineData("the 12.47 one", false)]
    [InlineData(null, false)]
    public void Only_real_words_name_a_row(string? said, bool names) => Assert.Equal(names, Ledger.Names(said));

    [Fact]
    public void Ticking_keeps_the_rows_sign_and_writes_the_amount_only_when_it_changed()
    {
        var month = November();
        Assert.Equal([new("'November'!E7", true), new("'November'!C7", -60m)], Ledger.PlanTick(month, month.Rows[5], 60));
        Assert.Equal([new CellWrite("'November'!E7", true)], Ledger.PlanTick(month, month.Rows[5], 58));
        Assert.Equal(1600m, Ledger.PlanTick(month, month.Rows[2], 1600)[1].Value); // a paycheck stays positive
    }

    [Fact]
    public void A_new_row_goes_in_date_order_chained_to_the_row_above_and_the_moved_row_rejoins_it()
    {
        var plan = Ledger.PlanAdd(November(), Nov16, "New Tire", -380, unexpected: true);

        Assert.Equal(8, plan.At); // before Water softener (Nov 20), after Water (Nov 15)
        Assert.Equal(
        [
            new("'November'!A8", "11/16/2026"),
            new("'November'!B8", "'New Tire"),
            new("'November'!C8", -380m),
            new("'November'!D8", "=D7+C8"),
            new("'November'!E8", true),
            new("'November'!D9", "=D8+C9"), // Water softener, moved down, now follows the new row
        ], plan.Writes);
    }

    [Fact]
    public void A_moved_row_with_a_typed_balance_is_left_alone()
    {
        var month = November();
        month.Rows[6] = month.Rows[6] with { BalanceFormula = null };
        Assert.DoesNotContain(Ledger.PlanAdd(month, Nov16, "Tire", -380, true).Writes, w => w.Range == "'November'!D9");
    }

    [Fact]
    public void The_unexpected_box_is_written_only_where_the_tab_has_that_column()
    {
        var plan = Ledger.PlanAdd(November(Columns with { Unexpected = 5 }), Nov16, "Tire", -380, unexpected: true);
        Assert.Contains(new CellWrite("'November'!F8", true), plan.Writes);
    }

    [Theory]
    [InlineData("2026-12-31", 11)] // later than everything: above the last row, so the summary's ranges still cover it
    [InlineData("2026-10-01", 3)]  // earlier than everything: still under the starting balance
    public void New_rows_stay_inside_the_ledger(string date, int at) =>
        Assert.Equal(at, Ledger.InsertAt(November().Rows, DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("=D6+C7", true)]
    [InlineData("=SUM(D6+C7)", true)]
    [InlineData("= d6 + c7", true)]
    [InlineData("=D5+C7", false)]
    [InlineData("=D6-C7", false)]
    [InlineData(null, false)]
    public void Only_a_balance_chained_from_the_row_above_counts(string? formula, bool chains) =>
        Assert.Equal(chains, Ledger.Chains(formula, Columns, 6));

    [Fact]
    public void Status_reads_out_unpaid_bills_money_coming_and_the_balances()
    {
        Assert.Equal(
            "November: 6 bills left to pay, $628 in all: Groceries - shop 2 $150 on Nov 14, Water $58 on Nov 15, Water softener $40 on Nov 20, " +
            "Groceries - shop 3 $150 on Nov 21, Car insurance $190 on Nov 28, and 1 more. Still coming in: Paycheck $1,500 on Nov 5. " +
            "Lowest balance $200, after Rent on Nov 1. Ending balance $922.",
            Ledger.Status(November()));
    }

    [Theory]
    [InlineData(58, "$58")]
    [InlineData(58.2, "$58.20")]
    [InlineData(-312, "minus $312")]
    [InlineData(1240.5, "$1,240.50")]
    public void Money_reads_naturally(decimal amount, string said) => Assert.Equal(said, Ledger.Money(amount));
}

public class RequestTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("november", true)]
    [InlineData("Novembr", false)]
    [InlineData("'!A1", false)]
    public void Month_must_be_a_month_name(string? month, bool ok) => Assert.Equal(ok, Requests.MonthProblem(month) is null);

    [Theory]
    [InlineData("Tire", 380, true)]
    [InlineData("Tire", 0, false)]
    [InlineData("Tire", 12.345, false)]
    [InlineData("Tire", 100001, false)]
    [InlineData("", 5, false)]
    [InlineData("Tire\n=1", 5, false)]
    public void Expenses_are_checked_before_they_reach_the_sheet(string item, decimal amount, bool ok) =>
        Assert.Equal(ok, Requests.Problem(new ExpenseRequest(item, amount, false, false)) is null);

    [Theory]
    [InlineData("water", null, true)]
    [InlineData(null, 9.5, true)]
    [InlineData("", 9.5, true)]
    [InlineData(null, null, false)]
    [InlineData("  ", null, false)]
    [InlineData(null, 0d, false)]
    [InlineData("water", 12.345, false)]
    public void A_lookup_needs_a_name_or_an_amount(string? item, double? amount, bool ok) =>
        Assert.Equal(ok, Requests.Problem(new FindRequest(item, (decimal?)amount)) is null);

    [Fact]
    public void A_paid_request_needs_a_real_row_and_a_quote_free_tab()
    {
        Assert.Null(Requests.Problem(new PaidRequest("November", 7, "Water", null)));
        Assert.NotNull(Requests.Problem(new PaidRequest("November", 1, "Water", null)));
        Assert.NotNull(Requests.Problem(new PaidRequest("Nov'!A1", 7, "Water", null)));
    }
}
