using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ekko.Budget;

/// <summary>Where each part of a month tab lives: 0-based columns, found from the header row.</summary>
public sealed record Layout(int Date, int Item, int Amount, int Balance, int Complete, int? Unexpected);

/// <summary>One ledger row. Number is the sheet's 1-based row. Amount is signed: money out is negative.</summary>
/// <param name="BalanceFormula">The Balance cell's formula ("=D5+C6"), or null where a value was typed.</param>
public sealed record Row(int Number, DateOnly? Date, string Item, decimal? Amount, decimal? Balance, bool Complete, bool Unexpected, string? BalanceFormula)
{
    public bool IsStart => Item.Equals("Starting balance", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A month tab as read: "November" (or "November 2026"), its numeric id, and the ledger rows in sheet order.</summary>
public sealed record Month(string Tab, long TabId, Layout Layout, List<Row> Rows);

/// <summary>One cell to write, as if typed. Range is A1 notation with the tab: 'November'!C7.</summary>
public sealed record CellWrite(string Range, object Value);

/// <summary>Where a new row goes and every cell to write once it's inserted.</summary>
public sealed record AddPlan(int At, List<CellWrite> Writes);

/// <summary>
/// The month tab's rules as plain code, with no Google calls, so all of it is unit-tested. The sheet is the user's: the
/// Balance column is their formula chain (each row = row above + its amount), and a write must leave that chain intact.
/// </summary>
public static partial class Ledger
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    public static Layout? FindLayout(IReadOnlyList<JsonNode?> header)
    {
        int? Find(params string[] names)
        {
            for (var i = 0; i < header.Count; i++)
                if (header[i]?.ToString().Trim() is { Length: > 0 } text && names.Any(n => text.Equals(n, StringComparison.OrdinalIgnoreCase)))
                    return i;
            return null;
        }
        if (Find("Date") is not { } date || Find("Item", "Description") is not { } item || Find("Amount") is not { } amount || Find("Balance") is not { } balance)
            return null;
        // Some tabs head the checkbox column "SpentSoFar"; whatever its name, it's the one after Balance.
        var complete = Find("Complete", "Completed", "Done", "Paid", "SpentSoFar", "Spent So Far") ?? balance + 1;
        return new Layout(date, item, amount, balance, complete, Find("Unexpected", "Unplanned"));
    }

    /// <summary>Rows from the second down to the first with no item. values and formulas are the same range, read both ways.</summary>
    public static List<Row> ReadRows(List<List<JsonNode?>> values, List<List<JsonNode?>> formulas, Layout layout)
    {
        var rows = new List<Row>();
        for (var i = 1; i < values.Count; i++)
        {
            var cells = values[i];
            var item = Cell(cells, layout.Item)?.ToString().Trim() ?? "";
            if (item.Length == 0)
                break;
            var formula = i < formulas.Count ? Cell(formulas[i], layout.Balance)?.ToString() : null;
            rows.Add(new Row(i + 1, Day(Cell(cells, layout.Date)), item, Number(Cell(cells, layout.Amount)), Number(Cell(cells, layout.Balance)),
                Flag(Cell(cells, layout.Complete)), layout.Unexpected is { } u && Flag(Cell(cells, u)),
                formula is { } f && f.StartsWith('=') ? f : null));
        }
        return rows;
    }

    // ------------------------------------------------------------ reading

    public static string Status(Month month)
    {
        var body = month.Rows.Where(r => !r.IsStart).ToList();
        var bills = body.Where(r => !r.Complete && r.Amount < 0).ToList();
        var coming = body.Where(r => !r.Complete && r.Amount > 0).ToList();
        var parts = new List<string>
        {
            bills.Count == 0
                ? $"{month.Tab}: every bill is checked off."
                : $"{month.Tab}: {bills.Count} {(bills.Count == 1 ? "bill" : "bills")} left to pay, {Money(-bills.Sum(r => r.Amount!.Value))} in all: {List(bills, 5)}.",
        };
        if (coming.Count > 0)
            parts.Add($"Still coming in: {List(coming, 3)}.");
        if (Lowest(month) is { } lowest)
            parts.Add($"Lowest balance {Money(lowest.Balance!.Value)}, after {lowest.Item}{On(lowest.Date)}.");
        if (month.Rows.LastOrDefault(r => r.Balance is not null) is { } last)
            parts.Add($"Ending balance {Money(last.Balance!.Value)}.");
        if (month.Layout.Unexpected is not null && body.Where(r => r.Unexpected && r.Amount < 0).Sum(r => r.Amount!.Value) is var surprise and < 0)
            parts.Add($"{Money(-surprise)} unexpected so far.");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// The numbers <see cref="Status"/> reads out, kept as numbers. Bills are what Status counts: unchecked money out.
    /// The message stays as it was: the desk shows it.
    /// </summary>
    /// <param name="nextMonth">Next month's tab, for <see cref="UntilPayday"/> once this month has no paycheck left; null if there's none.</param>
    public static Glance Glance(Month month, Month? nextMonth = null)
    {
        var bills = month.Rows.Where(Unpaid).ToList();
        var left = -bills.Sum(r => r.Amount!.Value);
        var next = bills.OrderBy(r => r.Date ?? DateOnly.MaxValue).FirstOrDefault(); // stable: same date keeps sheet order
        var ending = month.Rows.LastOrDefault(r => r.Balance is not null)?.Balance;
        var parts = new List<string>
        {
            bills.Count == 0 ? "Every bill is checked off." : $"{bills.Count} {(bills.Count == 1 ? "bill" : "bills")} left, {Money(left)} in all.",
        };
        if (next is not null)
            parts.Add($"Next: {next.Item}, {Money(-next.Amount!.Value)}{On(next.Date)}.");
        if (ending is { } e)
            parts.Add($"Ending balance {Money(e)}.");
        return new Glance("ok", string.Join(" ", parts), bills.Count, left, ending, next is null ? null : ToEntry(month, next),
            Lowest(month) is { } low ? new Low(low.Balance!.Value, low.Item, low.Date) : null, UntilPayday(month, nextMonth));
    }

    /// <summary>The row with the tab's lowest balance: the one Status reads out, and the sheet's MIN(D2:D31). Null if no row has a balance.</summary>
    public static Row? Lowest(Month month) => month.Rows.Where(r => r.Balance is not null).MinBy(r => r.Balance);

    /// <summary>
    /// Money in named "paycheck". Not just any money in: stipends, bonuses, cashback and eBay sales come in too, and on
    /// a tab a stipend can be dated before the first paycheck (DR-45).
    /// </summary>
    public static bool IsPaycheck(Row row) => !row.IsStart && row.Amount > 0 && Words(row.Item).Contains("paycheck");

    /// <summary>The tab's earliest-dated unchecked paycheck (the first in sheet order on a tie), or null.</summary>
    public static Row? Paycheck(Month? month) => month?.Rows.Where(r => !r.Complete && r.Date is not null && IsPaycheck(r)).MinBy(r => r.Date);

    /// <summary>
    /// The next unchecked paycheck and the unchecked bills to cover before it: overdue ones too, but not the paycheck's
    /// own day (it covers those), and undated ones last. With no paycheck left this month, next month's first one, after
    /// the rest of this month's bills. With none on either tab, every unchecked bill this month and no paycheck.
    /// </summary>
    public static Payday UntilPayday(Month month, Month? nextMonth)
    {
        // A tab's unchecked bills; with a paycheck, only those dated before it (or not dated at all).
        static IEnumerable<Entry> Bills(Month m, Row? pay) =>
            m.Rows.Where(r => Unpaid(r) && (pay is null || r.Date is null || r.Date < pay.Date)).Select(r => ToEntry(m, r));

        IEnumerable<Entry> bills;
        var payMonth = month;
        var pay = Paycheck(month);
        if (pay is not null)
            bills = Bills(month, pay);
        else if ((pay = Paycheck(nextMonth)) is not null)
            (bills, payMonth) = (Bills(month, null).Concat(Bills(nextMonth!, pay)), nextMonth!);
        else
            bills = Bills(month, null);

        var ordered = bills.OrderBy(b => b.Date ?? DateOnly.MaxValue).ToList(); // stable: a tie keeps sheet order, undated go last
        return new Payday(pay is null ? null : ToEntry(payMonth, pay), ordered, ordered.Sum(b => b.Amount));
    }

    /// <summary>
    /// Pay periods across tabs, in date order (DR-47). Balances are the sheet's own cells, never added up here: each tab's
    /// Starting balance links the one before, and a checkbox never changes a balance. Undated rows have no place on it.
    /// </summary>
    /// <param name="lastMonth">Last month's tab, or null if there's none.</param>
    /// <param name="after">The tabs after this month's, in order, as many as were read.</param>
    public static Timeline Timeline(DateOnly today, Month? lastMonth, Month month, IReadOnlyList<Month> after)
    {
        var first = new DateOnly(today.Year, today.Month, 1);
        // A later tab counts only rows dated in its own month or after. December's "11/08" is a typo for 12/08, and January's
        // tab still carries December's dates: taken as written, they'd put December's bills and paydays in the wrong place.
        static IEnumerable<(Month Tab, Row Row)> On(Month? tab, DateOnly from) =>
            tab is null ? [] : tab.Rows.Where(r => r.Date >= from).Select(r => (tab, r));
        var rows = On(lastMonth, DateOnly.MinValue).Concat(On(month, DateOnly.MinValue))
            .Concat(after.SelectMany((tab, i) => On(tab, first.AddMonths(i + 1))))
            .OrderBy(x => x.Row.Date) // stable: a tie keeps tab and sheet order
            .ToList();

        var paydays = rows.Where(x => IsPaycheck(x.Row) && x.Row.Date > today).Take(3).Select(x => new PayDate(x.Row.Date!.Value, x.Row.Balance)).ToList();
        var through = first.AddMonths(after.Count + 1).AddDays(-1);
        var bills = rows.Where(x => !x.Row.IsStart && x.Row.Amount < 0).ToList();
        var from = bills.FirstOrDefault(x => !x.Row.Complete).Row?.Date ?? today;
        var until = paydays.Count > 0 ? paydays[^1].Date.AddDays(-1) : through;
        // Every tab read, whole, whatever the bills leave out: the same low the glance gives and the sheet's box shows.
        List<MonthLow> lows = [];
        foreach (var (tab, start) in new[] { (lastMonth, first.AddMonths(-1)), (month, first) }.Concat(after.Select((m, i) => ((Month?)m, first.AddMonths(i + 1)))))
            if (tab is not null && Lowest(tab) is { } low)
                lows.Add(new MonthLow(start.ToString("yyyy-MM", CultureInfo.InvariantCulture), low.Balance!.Value));
        return new Timeline("ok", "", today,
            // By date, not sheet order: a misdated row below the paycheck would otherwise make "now" the balance after payday.
            month.Rows.Where(r => r.Date <= today && r.Balance is not null).OrderBy(r => r.Date).LastOrDefault()?.Balance,
            rows.LastOrDefault(x => IsPaycheck(x.Row) && x.Row.Date <= today).Row?.Date,
            paydays,
            bills.Where(x => x.Row.Date >= from && x.Row.Date <= until)
                .Select(x => new Bill(x.Tab.Tab, x.Row.Number, x.Row.Item, -x.Row.Amount!.Value, x.Row.Date!.Value, x.Row.Complete, x.Row.Balance))
                .ToList(),
            through, lows);
    }

    private static bool Unpaid(Row r) => !r.IsStart && !r.Complete && r.Amount < 0;

    private static Entry ToEntry(Month month, Row r) => new(r.Item, Math.Abs(r.Amount!.Value), r.Date, month.Tab, r.Number);

    /// <summary>
    /// The unchecked rows a request means. By name when the words name something: every word said starts a word of the
    /// row's item ("water", "verizon bill"); an amount alongside is then what it really came to, not part of the lookup.
    /// By amount when they don't ("the 12.47 one", "an expense, $12.47"): rows planned at exactly that much.
    /// Rows with the same name collapse to the one dated nearest today (the four "Groceries" rows mean the next one), and a
    /// row named exactly as said wins outright ("water" over "Water softener"). More than one left means ask.
    /// </summary>
    public static List<Row> FindUnchecked(Month month, string said, DateOnly today, decimal? amount = null)
    {
        var words = Key(said);
        if (words.Count == 0 && amount is null)
            return [];
        Func<Row, bool> fits = words.Count > 0
            ? r => words.All(w => Words(r.Item).Any(x => x.StartsWith(w, StringComparison.Ordinal)))
            : r => Math.Abs(r.Amount!.Value) == amount;
        var matches = month.Rows
            .Where(r => !r.IsStart && !r.Complete && r.Amount is not null and not 0m && fits(r))
            .GroupBy(r => string.Join(' ', Key(r.Item))) // "Groceries - shop 3" and "- shop 4" are one name: numbers don't count
            .Select(g => g.MinBy(r => r.Date is { } d ? Math.Abs(d.DayNumber - today.DayNumber) : 99)!)
            .ToList();
        return words.Count > 0 && matches.Where(r => Key(r.Item).SequenceEqual(words)).ToList() is [var exact] ? [exact] : matches;
    }

    /// <summary>Do these words name a row, or only point at one ("the expense", "the 12.47 one")?</summary>
    public static bool Names(string? said) => Key(said ?? "").Count > 0;

    /// <summary>"Water ($58 on Nov 15)": how a row is read back in a question.</summary>
    public static string Label(Row row) => $"{row.Item} ({Money(Math.Abs(row.Amount ?? 0))}{On(row.Date)})";

    // ------------------------------------------------------------ writing

    /// <summary>The cells that check a row off, plus its amount when what really happened differs from the plan.</summary>
    /// <param name="actual">Dollars, positive, as the user said them; the row's own sign is kept.</param>
    public static List<CellWrite> PlanTick(Month month, Row row, decimal? actual)
    {
        var writes = new List<CellWrite> { new(Cell(month, month.Layout.Complete, row.Number), true) };
        if (actual is { } a && row.Amount is { } planned && Math.Sign(planned) * a is var signed && signed != planned)
            writes.Add(new(Cell(month, month.Layout.Amount, row.Number), signed));
        return writes;
    }

    /// <summary>
    /// A new row in date order, with its balance chained to the row above. Inserting a row shifts the rows under it, and
    /// Google adjusts their formulas so the moved row still points at the row above the new one - skipping it. So the
    /// moved row's balance is rewritten to chain from the new row, but only if it was chained before.
    /// </summary>
    public static AddPlan PlanAdd(Month month, DateOnly date, string item, decimal signedAmount, bool unexpected)
    {
        var l = month.Layout;
        var at = InsertAt(month.Rows, date);
        string B(int row) => $"{Column(l.Balance)}{row}";
        string A(int row) => $"{Column(l.Amount)}{row}";
        var writes = new List<CellWrite>
        {
            new(Cell(month, l.Date, at), date.ToString("M/d/yyyy", CultureInfo.InvariantCulture)),
            new(Cell(month, l.Item, at), "'" + item), // the apostrophe makes it text as typed: never a formula, number or date
            new(Cell(month, l.Amount, at), signedAmount),
            new(Cell(month, l.Balance, at), $"={B(at - 1)}+{A(at)}"),
            new(Cell(month, l.Complete, at), true),
        };
        if (l.Unexpected is { } u)
            writes.Add(new(Cell(month, u, at), unexpected));
        if (month.Rows.FirstOrDefault(r => r.Number == at) is { } below && Chains(below.BalanceFormula, l, at - 1))
            writes.Add(new(Cell(month, l.Balance, at + 1), $"={B(at)}+{A(at + 1)}"));
        return new AddPlan(at, writes);
    }

    /// <summary>
    /// The sheet row a new row goes in, so dates stay in order: before the first later-dated row, else before the first
    /// undated one. Never above the starting balance, and never below the last row: the summary box's ranges (MIN(D2:D31))
    /// only grow when a row lands inside them.
    /// </summary>
    public static int InsertAt(List<Row> rows, DateOnly date)
    {
        if (rows.Count == 0)
            return 2;
        var body = rows.Where(r => !r.IsStart).ToList();
        var at = body.FirstOrDefault(r => r.Date > date)?.Number
                 ?? body.FirstOrDefault(r => r.Date is null)?.Number
                 ?? rows[^1].Number;
        return Math.Max(at, rows[0].IsStart ? rows[0].Number + 1 : rows[0].Number);
    }

    /// <summary>"=D6+C7" (or "=SUM(D6+C7)", in this layout's columns) chains from row 6.</summary>
    public static bool Chains(string? formula, Layout layout, int previousRow)
    {
        if (formula is null)
            return false;
        var m = Regex.Match(formula, $@"^=(?:SUM\()?\s*{Column(layout.Balance)}(?<row>\d+)\s*\+\s*{Column(layout.Amount)}\d+\s*\)?$", RegexOptions.IgnoreCase);
        return m.Success && int.Parse(m.Groups["row"].Value, CultureInfo.InvariantCulture) == previousRow;
    }

    // ------------------------------------------------------------ helpers

    /// <summary>"$58", "$58.20", "minus $312": read out loud as written.</summary>
    public static string Money(decimal amount)
    {
        var abs = Math.Abs(amount);
        var text = abs.ToString(abs == decimal.Truncate(abs) ? "C0" : "C2", En);
        return amount < 0 ? "minus " + text : text;
    }

    public static string Cell(Month month, int column, int row) => $"'{month.Tab}'!{Column(column)}{row}";

    public static string Column(int index) => ((char)('A' + index)).ToString();

    private static string On(DateOnly? date) => date is { } d ? $" on {d.ToString("MMM d", En)}" : "";

    private static string List(List<Row> rows, int max)
    {
        var named = rows.Take(max).Select(r => $"{r.Item} {Money(Math.Abs(r.Amount!.Value))}{On(r.Date)}").ToList();
        return string.Join(", ", named) + (rows.Count > max ? $", and {rows.Count - max} more" : "");
    }

    /// <summary>Lower-case words, apostrophes dropped so "Sam's" and "sams" agree.</summary>
    private static List<string> Words(string text) =>
        WordPattern().Matches(text.ToLowerInvariant().Replace("'", "").Replace("’", "")).Select(m => m.Value).ToList();

    /// <summary>The words that name something: "the water bill" -> water. Numbers are amounts or row counters, not names.</summary>
    private static List<string> Key(string text) =>
        Words(text).Where(w => w.Length >= 3 && !Filler.Contains(w) && !w.All(char.IsAsciiDigit)).ToList();

    private static readonly HashSet<string> Filler =
    [
        "the", "bill", "bills", "payment", "paid", "for", "and", "was", "has", "came", "went", "through",
        "expense", "expenses", "spreadsheet", "sheet", "one", // "mark an expense off my spreadsheet" names no row
    ];

    private static JsonNode? Cell(List<JsonNode?> cells, int column) => column < cells.Count ? cells[column] : null;

    private static DateOnly? Day(JsonNode? cell) =>
        cell?.ToString().Trim() is { Length: > 0 } text
        && DateOnly.TryParseExact(text, ["M/d/yyyy", "M/d/yy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static decimal? Number(JsonNode? cell)
    {
        if (cell is JsonValue v && v.TryGetValue(out double d))
            return Math.Round((decimal)d, 2);
        var text = cell?.ToString().Replace("$", "").Replace(",", "").Trim() ?? "";
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static bool Flag(JsonNode? cell) =>
        cell is JsonValue v && v.TryGetValue(out bool b) ? b : string.Equals(cell?.ToString(), "TRUE", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordPattern();
}
