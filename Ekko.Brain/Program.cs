using Ekko.Brain;
using Ekko.Brain.Pipeline;
using Ekko.Brain.ServerOps;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var keys = config.GetSection("Api:Keys").Get<Dictionary<string, string>>() ?? [];
if (keys.Count == 0 && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Api:Keys needs at least one client key outside Development: /message can lock the PC.");

builder.Services.AddEkko(config, builder.Environment.ContentRootPath);

var app = builder.Build();
if (string.IsNullOrWhiteSpace(config["ANTHROPIC_API_KEY"]))
    app.Logger.LogWarning("ANTHROPIC_API_KEY isn't set: steps that fall back to Claude will give up instead.");

app.Use(async (context, next) =>
{
    if (keys.Count == 0 || ApiKey.ClientFor(keys, context.Request.Headers[ApiKey.Header]) is not null)
    {
        await next();
        return;
    }
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await context.Response.WriteAsJsonAsync(new { error = $"missing or wrong {ApiKey.Header}" });
});

app.MapPost("/message", async (MessageRequest request, MessagePipeline pipeline, CancellationToken ct) =>
    request.Error() is { } error
        ? Results.BadRequest(new { error })
        : Results.Ok(await pipeline.HandleAsync(request, ct)));

// Server-ops runs waiting for approval, for clients other than the conversation that started them (the deck, a phone).
app.MapGet("/runs/waiting", (ServerOpsAgent agent) =>
    Results.Ok(agent.Waiting().Select(r => new { r.Id, r.ConversationId, r.Request, r.Question, r.Updated })));

app.MapPost("/runs/{id}/approve", async (string id, MessagePipeline pipeline, CancellationToken ct) =>
    Results.Ok(await pipeline.ResolveRunAsync(id, approved: true, "api", ct)));

app.MapPost("/runs/{id}/deny", async (string id, MessagePipeline pipeline, CancellationToken ct) =>
    Results.Ok(await pipeline.ResolveRunAsync(id, approved: false, "api", ct)));

// Recent steps for live views (the deck's Ekko page), trimmed by TraceView. Poll with the last seq seen; start over when boot changes.
app.MapGet("/trace", (long? after, TraceLog trace) => Results.Ok(new { trace.Boot, Events = trace.Since(after ?? 0) }));

app.Run();
