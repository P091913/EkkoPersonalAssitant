using Ekko.Files;

// Ekko's file store: photos and PDFs sent from a channel (Telegram, the deck), kept for the desk to show (DR-40).
// It reads only a file's first bytes, which say what it is, and never opens one.
var builder = WebApplication.CreateBuilder(args);

var keys = builder.Configuration.GetSection("Api:Keys").Get<Dictionary<string, string>>() ?? [];
if (keys.Count == 0 && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Api:Keys needs at least one client key outside Development: this service keeps your files.");
if (keys.ContainsKey(FileStore.Pc))
    throw new InvalidOperationException($"No client key may be named \"{FileStore.Pc}\": that name marks files in the PC folder.");

builder.Services.AddSingleton(TimeProvider.System);
// Files:PcFolder is your Downloads, mounted read-only (DR-41): photos and PDFs saved there are listed and served too.
builder.Services.AddSingleton(sp => new FileStore(Path.Combine(builder.Environment.ContentRootPath, "data", "files"),
    sp.GetRequiredService<TimeProvider>(), builder.Configuration["Files:PcFolder"]));

var app = builder.Build();

app.Use(async (context, next) =>
{
    var client = keys.Count == 0 ? "dev" : ApiKeys.ClientFor(keys, context.Request.Headers["X-Api-Key"]);
    if (client is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new Saved(false, "Missing or wrong X-Api-Key."));
        return;
    }
    context.Items["client"] = client;
    await next();
});

// One file per request, as the multipart field "file". Who sent it is whose key it came with.
// No antiforgery: there are no cookies here, only keys.
app.MapPost("/files", async (IFormFile? file, HttpContext context, FileStore store, CancellationToken ct) =>
{
    if (file is null)
        return Results.BadRequest(new Saved(false, "Send the file as the multipart field \"file\"."));
    // Kestrel stops any body over 30 MB before this, so the copy is bounded.
    using var bytes = new MemoryStream();
    await file.CopyToAsync(bytes, ct);
    var saved = store.Save(file.FileName, (string)context.Items["client"]!, bytes.ToArray());
    return saved.File is { } kept ? Results.Created($"/files/{kept.Id}", saved) : Results.BadRequest(saved);
}).DisableAntiforgery();

// q: words that pick files ("invoice", "photo"), as Ekko heard them; the desk passes them on unchanged.
app.MapGet("/files", (int? limit, string? q, FileStore store) => q is { Length: > 100 }
    ? Results.BadRequest(new Saved(false, "q is longer than 100 characters."))
    : Results.Ok(store.Recent(Math.Clamp(limit ?? 20, 1, 100), q)));

app.MapGet("/files/{id}", (string id, FileStore store, HttpContext context) =>
{
    if (store.Find(id) is not { } file)
        return Results.NotFound(new Saved(false, "There's no file with that id."));
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(store.PathOf(file), file.Type);
});

app.Run();
