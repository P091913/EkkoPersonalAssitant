using Ekko.Brain;
using Ekko.Brain.Pipeline;
using Ekko.Brain.ServerOps;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var apiKey = config["Api:Key"];
if (string.IsNullOrEmpty(apiKey) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Api:Key is required outside Development: /message can lock the PC.");

builder.Services.AddEkko(config, builder.Environment.ContentRootPath);

var app = builder.Build();
if (string.IsNullOrWhiteSpace(config["ANTHROPIC_API_KEY"]))
    app.Logger.LogWarning("ANTHROPIC_API_KEY isn't set: steps that fall back to Claude will give up instead.");

app.Use(async (context, next) =>
{
    if (ApiKey.Allows(apiKey, context.Request.Headers[ApiKey.Header]))
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

app.Run();
