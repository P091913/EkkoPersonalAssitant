using Ekko.Budget;

// Ekko's budget service: the only thing that touches the expenses sheet, as tvremote is the only thing that touches
// Jellyfin. No AI here - callers say what they mean in structured requests, and every rule of the sheet lives in code.
var builder = WebApplication.CreateBuilder(args);

var sheet = builder.Configuration.GetSection("Sheet").Get<SheetOptions>() ?? new SheetOptions();
if (sheet.SpreadsheetId.Length == 0 || sheet.KeyFile.Length == 0)
    throw new InvalidOperationException("Sheet:SpreadsheetId and Sheet:KeyFile are required.");

var keys = builder.Configuration.GetSection("Api:Keys").Get<Dictionary<string, string>>() ?? [];
if (keys.Count == 0 && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Api:Keys needs at least one client key outside Development: this service writes to your finances.");

builder.Services.AddSingleton(sheet);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("sheets", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<GoogleSheets>();
builder.Services.AddSingleton<BudgetBook>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    var client = keys.Count == 0 ? "dev" : ApiKeys.ClientFor(keys, context.Request.Headers["X-Api-Key"]);
    if (client is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new Answer("error", "Missing or wrong X-Api-Key."));
        return;
    }
    await next();
});

app.MapGet("/budget/status", (string? month, BudgetBook book, CancellationToken ct) =>
    Requests.MonthProblem(month) is { } p ? Bad(p) : Run(book.StatusAsync(month, ct)));

app.MapPost("/budget/bills/find", (FindRequest r, BudgetBook book, CancellationToken ct) =>
    Requests.Problem(r) is { } p ? Bad(p) : Run(book.FindAsync(r.Item, r.Amount, ct)));

app.MapPost("/budget/bills/paid", (PaidRequest r, BudgetBook book, CancellationToken ct) =>
    Requests.Problem(r) is { } p ? Bad(p) : Run(book.MarkPaidAsync(r, ct)));

app.MapPost("/budget/expenses", (ExpenseRequest r, BudgetBook book, CancellationToken ct) =>
    Requests.Problem(r) is { } p ? Bad(p) : Run(book.AddAsync(r, ct)));

app.Run();

// Every outcome is the same JSON shape with a sentence to read out; only bad requests and Google failures change the status code.
static Task<IResult> Bad(string problem) => Task.FromResult(Results.BadRequest(new Answer("error", problem)));

static async Task<IResult> Run(Task<Answer> work)
{
    try
    {
        return Results.Ok(await work);
    }
    catch (SheetsException ex)
    {
        return Results.Json(new Answer("error", ex.Message), statusCode: StatusCodes.Status502BadGateway);
    }
}
