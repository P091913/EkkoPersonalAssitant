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
    if (Talk.NeedsKey(context.Request.Path) && !Talk.KeyMatches(pageKey, Talk.SuppliedKey(context.Request.Headers)))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "missing or wrong key" });
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

// The Even app's own agent setting (DR-49): OpenAI's chat format, with the page key as its Bearer token.
app.MapPost("/v1/chat/completions", async (ChatRequest r, HttpRequest request, BrainClient brain, ILogger<Program> log, CancellationToken ct) =>
{
    // shortcut: Even doesn't document where calls come from or whether they stream; drop this line once a real call shows both.
    log.LogInformation("Even agent call: stream {Stream}, {Count} messages, from {From}, agent {Agent}",
        r.Stream, r.Messages?.Count ?? 0, request.Headers["X-Forwarded-For"].ToString(), request.Headers.UserAgent.ToString());
    var text = OpenAiChat.LastUserText(r);
    if (Talk.Problem(new AskRequest(text)) is { } problem)
        return Results.BadRequest(new { error = new { message = problem } });
    var reply = await brain.AskAsync(text!.Trim(), ct);
    return r.Stream ? Results.Text(OpenAiChat.Stream(reply), "text/event-stream") : Results.Ok(OpenAiChat.Completion(reply));
});

app.Run();
