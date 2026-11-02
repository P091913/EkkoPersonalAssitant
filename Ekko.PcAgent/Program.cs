using System.Security.Cryptography;
using System.Text;
using Ekko.PcAgent;

// Ekko's PC agent: the one piece that must run natively in your Windows session, because pressing keys and
// locking the PC need a desktop. It does only what Actions in config names - callers send an id, never keys or paths.
var builder = WebApplication.CreateBuilder(args);

var actions = builder.Configuration.GetSection("Actions").Get<Dictionary<string, PcAction>>() ?? [];
foreach (var (id, action) in actions)
    if (action.Problem() is { } problem)
        throw new InvalidOperationException($"Actions:{id}: {problem}");

var apiKey = builder.Configuration["Api:Key"];
if (string.IsNullOrEmpty(apiKey) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Api:Key is required outside Development: this agent can lock the PC.");

var app = builder.Build();

app.Use(async (context, next) =>
{
    var supplied = context.Request.Headers["X-Api-Key"].ToString();
    if (string.IsNullOrEmpty(apiKey) || CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(apiKey), Encoding.UTF8.GetBytes(supplied)))
    {
        await next();
        return;
    }
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await context.Response.WriteAsJsonAsync(new { ok = false, error = "missing or wrong X-Api-Key" });
});

// Two key sequences must never interleave: a press while another runs is refused (429), not queued.
var busy = new SemaphoreSlim(1, 1);

app.MapPost("/action/{id}", async (string id, ILogger<PcAction> log, CancellationToken ct) =>
{
    if (!actions.TryGetValue(id, out var action))
        return Results.NotFound(new { ok = false, error = $"no action named '{id}'" });
    if (!busy.Wait(0))
        return Results.Json(new { ok = false, error = "another action is running" }, statusCode: StatusCodes.Status429TooManyRequests);
    try
    {
        var detail = await action.RunAsync(ct);
        log.LogInformation("{Action}: {Detail}", id, detail);
        return Results.Ok(new { ok = true, action = id, detail });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        log.LogError(ex, "{Action} failed", id);
        return Results.Json(new { ok = false, action = id, error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
    finally
    {
        busy.Release();
    }
});

app.Run();
