using Ekko.PcAgent;

// Ekko's PC agent: the one piece that must run natively in your Windows session, because pressing keys, locking the PC
// and reading media sessions need a desktop. It does only what Actions in config names - callers send an id, never keys
// or paths. It listens on localhost; `tailscale serve` exposes it to the tailnet. Each client has its own key.
var builder = WebApplication.CreateBuilder(args);

var actions = builder.Configuration.GetSection("Actions").Get<Dictionary<string, PcAction>>() ?? [];
foreach (var (id, action) in actions)
    if (action.Problem() is { } problem)
        throw new InvalidOperationException($"Actions:{id}: {problem}");

var keys = builder.Configuration.GetSection("Api:Keys").Get<Dictionary<string, string>>() ?? [];
if (keys.Count == 0 && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Api:Keys needs at least one client key outside Development: this agent can lock the PC.");

var app = builder.Build();

app.Use(async (context, next) =>
{
    // No keys configured = open, Development only (checked above).
    var client = keys.Count == 0 ? "dev" : ApiKeys.ClientFor(keys, context.Request.Headers["X-Api-Key"]);
    if (client is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { ok = false, error = "missing or wrong X-Api-Key" });
        return;
    }
    context.Items["client"] = client;
    await next();
});

// Two key sequences must never interleave: a press while another runs is refused (429), not queued.
var busy = new SemaphoreSlim(1, 1);

app.MapPost("/action/{id}", async (string id, HttpContext context, ILogger<PcAction> log, CancellationToken ct) =>
{
    if (!actions.TryGetValue(id, out var action))
        return Results.NotFound(new { ok = false, error = $"no action named '{id}'" });
    if (!busy.Wait(0))
        return Results.Json(new { ok = false, error = "another action is running" }, statusCode: StatusCodes.Status429TooManyRequests);
    var client = context.Items["client"];
    try
    {
        var detail = await action.RunAsync(ct);
        log.LogInformation("{Client} -> {Action}: {Detail}", client, id, detail);
        return Results.Ok(new { ok = true, action = id, detail });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        log.LogError(ex, "{Client} -> {Action} failed", client, id);
        return Results.Json(new { ok = false, action = id, error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
    finally
    {
        busy.Release();
    }
});

app.MapGet("/nowplaying", async (ILogger<PcAction> log, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await NowPlaying.ReadAsync(ct));
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // A deck widget polls this: report "unavailable", don't throw.
        log.LogWarning(ex, "Reading the media session failed");
        return Results.Json(new { active = false, error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();
