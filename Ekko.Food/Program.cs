using Ekko.Food;

// Ekko's food service: your food list and diary, in one SQLite file. No AI here - the Brain sends foods as words, and
// matching them to the list, the math and every sentence live in code (DR-35, DR-36).
var builder = WebApplication.CreateBuilder(args);

var keys = builder.Configuration.GetSection("Api:Keys").Get<Dictionary<string, string>>() ?? [];
if (keys.Count == 0 && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Api:Keys needs at least one client key outside Development: this service keeps your food diary.");

var path = Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Food:DatabasePath"] ?? "data/food.db");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new FoodDiary(path, sp.GetRequiredService<TimeProvider>()));

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

app.MapPost("/food/log", (LogRequest r, FoodDiary diary) =>
    Requests.Problem(r) is { } p ? Bad(p) : Results.Ok(diary.Log(r.Items!.OfType<LogItem>().ToList())));

app.MapGet("/food/day", (string? day, FoodDiary diary) => day switch
{
    null or "today" => Results.Ok(diary.Day(0)),
    "yesterday" => Results.Ok(diary.Day(1)),
    _ => Bad("day must be today or yesterday."),
});

app.MapPost("/food/undo", (FoodDiary diary) => Results.Ok(diary.Undo()));

app.MapGet("/food/foods", (string? food, FoodDiary diary) =>
    Requests.FoodProblem(food) is { } p ? Bad(p) : Results.Ok(diary.Find(food!)));

app.MapPost("/food/foods", (FoodRequest r, FoodDiary diary) =>
    Requests.Problem(r) is { } p ? Bad(p) : Results.Ok(diary.SetCalories(r.Food!, r.CaloriesEach!.Value)));

app.Run();

// Every outcome is the same JSON shape with a sentence to read out.
static IResult Bad(string problem) => Results.BadRequest(new Answer("error", problem));
