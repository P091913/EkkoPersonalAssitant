using Ekko.Glasses;
using Microsoft.AspNetCore.Mvc;

// Ekko on the Even G2 (DR-38): serves the page the Even app runs, turns what was said into text, and passes it to the
// Brain like the deck and Telegram do. Published on this PC's loopback only; `tailscale serve` puts it on the tailnet.
var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var pageKey = config["Glasses:PageKey"];
var brainUrl = config["Brain:BaseUrl"] ?? throw new InvalidOperationException("Brain:BaseUrl is required.");
var brainKey = config["Brain:ApiKey"];
if ((string.IsNullOrEmpty(pageKey) || string.IsNullOrEmpty(brainKey)) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Glasses:PageKey and Brain:ApiKey are required outside Development: what's said here can lock the PC.");

builder.Services.AddSingleton(config.GetSection("Speech").Get<SpeechOptions>() ?? new SpeechOptions());
builder.Services.AddHttpClient<BrainClient>(c =>
{
    c.BaseAddress = new Uri(brainUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(150); // a server-ops run can take up to 120 s
    if (!string.IsNullOrEmpty(brainKey))
        c.DefaultRequestHeaders.Add("X-Api-Key", brainKey);
});
builder.Services.AddHttpClient<Speech>(c =>
{
    c.BaseAddress = new Uri((config["Speech:BaseUrl"] ?? "http://localhost:8000/").TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(60); // the first request after a restart loads the model
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (Talk.NeedsKey(context.Request.Path) && !Talk.KeyMatches(pageKey, context.Request.Headers["X-Api-Key"]))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "missing or wrong X-Api-Key" });
        return;
    }
    await next();
});

// The page: no-cache, so a change shows on the glasses the next time it's opened.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = f => f.Context.Response.Headers.CacheControl = "no-cache" });

app.MapPost("/hear", async (HttpRequest request, Speech speech, CancellationToken ct) =>
{
    if (Talk.FileName(request.ContentType) is not { } fileName)
        return Results.BadRequest(new { error = "audio must be WAV, WebM, MP4 or Ogg." });
    using var audio = new MemoryStream();
    await request.Body.CopyToAsync(audio, ct);
    if (audio.Length < 1000)
        return Results.BadRequest(new { error = "that recording was empty." });
    return await speech.TranscribeAsync(audio.ToArray(), request.ContentType!, fileName, ct) is { } heard
        ? Results.Ok(new { text = heard })
        : Results.Json(new { error = "I couldn't transcribe that right now." }, statusCode: StatusCodes.Status502BadGateway);
}).WithMetadata(new RequestSizeLimitAttribute(Talk.MaxAudioBytes));

app.MapPost("/ask", async (AskRequest r, BrainClient brain, CancellationToken ct) =>
    Talk.Problem(r) is { } problem ? Results.BadRequest(new { error = problem }) : Results.Ok(new { reply = await brain.AskAsync(r.Text!.Trim(), ct) }));

app.Run();
