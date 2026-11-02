using Ekko.Brain;
using Ekko.Brain.Pipeline;

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

app.Run();
