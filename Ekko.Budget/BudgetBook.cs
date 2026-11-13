using System.Globalization;

namespace Ekko.Budget;

/// <summary>A row a request might mean. Tab, Row and Item go back unchanged in a /bills/paid request.</summary>
public sealed record Candidate(string Tab, int Row, string Item, decimal Amount, string Label);

/// <param name="Status">ok | found | ambiguous | not_found | already | conflict | error.</param>
/// <param name="Message">A sentence ready to read out.</param>
public sealed record Answer(string Status, string Message, IReadOnlyList<Candidate>? Candidates = null);

/// <summary>This month as numbers for a screen (the desk's Today card, the glasses), with the same status and sentence as an Answer.</summary>
/// <param name="LeftToPay">Dollars, positive: the unchecked bills added up.</param>
/// <param name="Next">The unchecked bill dated soonest, overdue ones first.</param>
/// <param name="Lowest">The tab's lowest balance, which can be negative.</param>
/// <param name="UntilPayday">The next paycheck and the bills to cover before it (DR-45).</param>
public sealed record Glance(string Status, string Message, int BillsLeft, decimal LeftToPay, decimal? EndingBalance, Entry? Next,
    Low? Lowest = null, Payday? UntilPayday = null);

/// <summary>A row for a screen. Amount is dollars, positive, whichever way the money went.</summary>
/// <param name="Tab">With <paramref name="Row"/> and <paramref name="Item"/>, what a /bills/paid request takes back unchanged.</param>
public sealed record Entry(string Item, decimal Amount, DateOnly? Date, string Tab, int Row);

public sealed record Low(decimal Balance, string Item, DateOnly? Date);

/// <param name="Paycheck">Null when neither this month's tab nor next month's has one left.</param>
/// <param name="Total">Dollars, positive: the bills added up.</param>
public sealed record Payday(Entry? Paycheck, IReadOnlyList<Entry> Bills, decimal Total);

/// <summary>Pay periods across month tabs for the glasses' timeline (DR-47). Every balance is a Balance cell as the sheet has it.</summary>
/// <param name="BalanceNow">The latest-dated row on or before today on this month's tab.</param>
/// <param name="LastPayday">The latest paycheck on or before today, checked off or not.</param>
/// <param name="Paydays">The next three paychecks after today.</param>
/// <param name="Bills">Money out from the oldest unchecked bill through the day before the last payday listed, checked ones too.</param>
/// <param name="CoversThrough">The last day of the last month tab read: nothing after it is known.</param>
/// <param name="MonthLows">Each tab read, oldest first, with its whole tab's lowest balance: the glance's lowest, the sheet's "Lowest balance this month".</param>
public sealed record Timeline(string Status, string Message, DateOnly Today, decimal? BalanceNow, DateOnly? LastPayday,
    IReadOnlyList<PayDate> Paydays, IReadOnlyList<Bill> Bills, DateOnly? CoversThrough, IReadOnlyList<MonthLow> MonthLows);

public sealed record PayDate(DateOnly Date, decimal? Balance);

/// <param name="Month">"2026-11".</param>
public sealed record MonthLow(string Month, decimal Balance);

/// <param name="Amount">Dollars, positive.</param>
public sealed record Bill(string Tab, int Row, string Item, decimal Amount, DateOnly Date, bool Paid, decimal? Balance);

/// <param name="Amount">Dollars, positive. Finds the row by amount when <paramref name="Item"/> names nothing.</param>
public sealed record FindRequest(string? Item, decimal? Amount);

/// <param name="Amount">What it really came to, in dollars, positive; only when it differs from the plan.</param>
public sealed record PaidRequest(string? Tab, int Row, string? Item, decimal? Amount);

public sealed record ExpenseRequest(string? Item, decimal Amount, bool Income, bool Planned);

/// <summary>The expenses sheet's month tabs: reading a month, finding a bill, checking it off, adding a row.</summary>
public sealed class BudgetBook(GoogleSheets sheets, TimeProvider clock, ILogger<BudgetBook> log)
{
    private const int MaxRows = 300;
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    // Writes read the tab, then write by row number: two at once could both insert at the same row.
    private readonly SemaphoreSlim _writing = new(1, 1);

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public async Task<Answer> StatusAsync(string? month, CancellationToken ct)
    {
        var name = month is { Length: > 0 } ? En.TextInfo.ToTitleCase(month.Trim().ToLowerInvariant()) : MonthName(Today);
        return await LoadAsync(name, ct) is { } loaded
            ? new Answer("ok", Ledger.Status(loaded))
            : new Answer("not_found", $"There's no {name} tab on the expenses sheet.");
    }

    /// <summary>This month: a glance is about now. Next month's tab only once this month has no paycheck left to wait for.</summary>
    public async Task<Glance> GlanceAsync(CancellationToken ct)
    {
        if (await LoadAsync(MonthName(Today), ct) is not { } month)
            return new Glance("not_found", $"There's no {MonthName(Today)} tab on the expenses sheet yet.", 0, 0, null, null);
        var nextMonth = Ledger.Paycheck(month) is null ? await LoadAsync(MonthName(Today.AddMonths(1)), ct) : null;
        return Ledger.Glance(month, nextMonth);
    }

    /// <summary>
    /// Last month's tab (the last payday, a late bill) and this month's, then the months after until three paydays are in
    /// view: at most two more, and only while their tabs exist.
    /// </summary>
    public async Task<Timeline> TimelineAsync(CancellationToken ct)
    {
        var today = Today;
        if (await LoadAsync(MonthName(today), ct) is not { } month)
            return new Timeline("not_found", $"There's no {MonthName(today)} tab on the expenses sheet yet.", today, null, null, [], [], null, []);
        var lastMonth = await LoadAsync(MonthName(today.AddMonths(-1)), ct);
        var after = new List<Month>();
        var timeline = Ledger.Timeline(today, lastMonth, month, after);
        while (timeline.Paydays.Count < 3 && after.Count < 2 && await LoadAsync(MonthName(today.AddMonths(after.Count + 1)), ct) is { } next)
        {
            after.Add(next);
            timeline = Ledger.Timeline(today, lastMonth, month, after);
        }
        return timeline;
    }

    /// <summary>This month's unchecked rows first; last month's if nothing this month fits (a late bill).</summary>
    public async Task<Answer> FindAsync(string? item, decimal? amount, CancellationToken ct)
    {
        // The same choice Ledger.FindUnchecked makes: by name if the words name something, otherwise by amount.
        var what = Ledger.Names(item) ? item!.Trim() : amount is { } a ? $"row for {Ledger.Money(a)}" : null;
        if (what is null)
            return new Answer("not_found", "Which one? Say its name or its amount.");
        foreach (var day in new[] { Today, Today.AddMonths(-1) })
        {
            if (await LoadAsync(MonthName(day), ct) is not { } month || Ledger.FindUnchecked(month, item ?? "", Today, amount) is not { Count: > 0 } rows)
                continue;
            var candidates = rows.Take(5).Select(r => new Candidate(month.Tab, r.Number, r.Item, r.Amount!.Value, Ledger.Label(r))).ToList();
            return candidates.Count == 1
                ? new Answer("found", $"Found {candidates[0].Label}.", candidates)
                : new Answer("ambiguous", $"Which one: {string.Join(", ", candidates.SkipLast(1).Select(c => c.Label))} or {candidates[^1].Label}?", candidates);
        }
        return new Answer("not_found", $"I couldn't find an unchecked {what} on this month's or last month's tab.");
    }

    /// <param name="client">Whose key asked, for the log.</param>
    public async Task<Answer> MarkPaidAsync(PaidRequest request, string client, CancellationToken ct)
    {
        await _writing.WaitAsync(ct);
        try
        {
            if (await LoadAsync(request.Tab!, ct) is not { } month)
                return new Answer("not_found", $"There's no {request.Tab} tab on the expenses sheet.");
            // The row number came from an earlier read; a row added since would have moved it.
            if (month.Rows.FirstOrDefault(r => r.Number == request.Row) is not { } row || !row.Item.Equals(request.Item!.Trim(), StringComparison.OrdinalIgnoreCase))
                return new Answer("conflict", "The sheet changed since I looked. Ask me again.");
            if (row.Complete)
                return new Answer("already", $"{row.Item} is already checked off.");

            var writes = Ledger.PlanTick(month, row, request.Amount);
            await sheets.WriteAsync(writes, ct);
            log.LogInformation("Checked off row {Row} ({Item}) on {Tab} for {Client}", row.Number, row.Item, month.Tab, client);
            var changed = writes.Count > 1 ? $", at {Ledger.Money(request.Amount!.Value)} instead of {Ledger.Money(Math.Abs(row.Amount!.Value))}" : "";
            return new Answer("ok", $"Checked off {row.Item} on {month.Tab}{changed}.");
        }
        finally
        {
            _writing.Release();
        }
    }

    public async Task<Answer> AddAsync(ExpenseRequest request, CancellationToken ct)
    {
        var item = En.TextInfo.ToTitleCase(request.Item!.Trim());
        var today = Today;
        await _writing.WaitAsync(ct);
        try
        {
            if (await LoadAsync(MonthName(today), ct) is not { } month)
                return new Answer("not_found", $"There's no {MonthName(today)} tab on the expenses sheet yet.");
            var unexpected = !request.Income && !request.Planned;
            var plan = Ledger.PlanAdd(month, today, item, request.Income ? request.Amount : -request.Amount, unexpected);
            await InsertRowAsync(month, plan.At, ct);
            await sheets.WriteAsync(plan.Writes, ct);
            log.LogInformation("Added row {Row} ({Item}) on {Tab}", plan.At, item, month.Tab);
            var how = request.Income ? "in" : unexpected && month.Layout.Unexpected is not null ? "spent, marked unexpected" : "spent";
            return new Answer("ok", $"Added {item} to {month.Tab}: {Ledger.Money(request.Amount)} {how}.");
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>
    /// An empty row at <paramref name="at"/>, with checkbox rules on its Complete and Unexpected cells: an inserted row
    /// isn't promised its neighbours' rules. Where the ledger is a Google "table", those columns are typed and Google
    /// refuses a rule there, and with it the whole batch; the table makes the checkboxes itself, so the row goes in alone.
    /// </summary>
    private async Task InsertRowAsync(Month month, int at, CancellationToken ct)
    {
        var insert = new
        {
            insertDimension = new { range = new { sheetId = month.TabId, dimension = "ROWS", startIndex = at - 1, endIndex = at }, inheritFromBefore = at > 2 },
        };
        var boxes = new[] { month.Layout.Complete, month.Layout.Unexpected }.OfType<int>().Select(column => (object)new
        {
            setDataValidation = new
            {
                range = new { sheetId = month.TabId, startRowIndex = at - 1, endRowIndex = at, startColumnIndex = column, endColumnIndex = column + 1 },
                rule = new { condition = new { type = "BOOLEAN" }, strict = true },
            },
        });
        try
        {
            await sheets.BatchUpdateAsync([insert, .. boxes], ct);
        }
        catch (SheetsException ex) when (ex.Message.Contains("HTTP 400"))
        {
            log.LogInformation("Checkbox rule refused on {Tab} (a table column); inserting the row alone", month.Tab);
            await sheets.BatchUpdateAsync([insert], ct);
        }
    }

    /// <summary>The tab named "November", or failing that one starting with it ("November 2026"). Null if none, or it has no ledger header.</summary>
    private async Task<Month?> LoadAsync(string name, CancellationToken ct)
    {
        var tabs = await sheets.TabsAsync(ct);
        var tab = tabs.Where(t => t.Title.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            .Concat(tabs.Where(t => t.Title.Trim().StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault();
        if (tab.Title is not { Length: > 0 })
            return null;
        var range = $"'{tab.Title}'!A1:Z{MaxRows}";
        var values = await sheets.ReadAsync(range, formulas: false, ct);
        if (values.Count == 0 || Ledger.FindLayout(values[0]) is not { } layout)
            return null;
        var formulas = await sheets.ReadAsync(range, formulas: true, ct);
        return new Month(tab.Title, tab.Id, layout, Ledger.ReadRows(values, formulas, layout));
    }

    private static string MonthName(DateOnly day) => day.ToString("MMMM", CultureInfo.InvariantCulture);
}
