using Ekko.Telegram;

// Ekko's Telegram channel: a client of the Brain like the deck, with its own key (DR-37). It holds the bot token,
// so the token never reaches the Brain's config. No web host: it only makes outgoing calls, so nothing listens.
var builder = Host.CreateApplicationBuilder(args);
var config = builder.Configuration;

var telegram = config.GetSection("Telegram").Get<TelegramOptions>() ?? new TelegramOptions();
if (telegram.BotToken.Length == 0 || telegram.Allowed.Count == 0)
    throw new InvalidOperationException("Telegram:BotToken and Telegram:AllowedUsers are required.");
var brainUrl = config["Brain:BaseUrl"] ?? throw new InvalidOperationException("Brain:BaseUrl is required.");
var brainKey = config["Brain:ApiKey"];
if (string.IsNullOrEmpty(brainKey) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Brain:ApiKey is required outside Development.");
var filesUrl = config["Files:BaseUrl"] ?? throw new InvalidOperationException("Files:BaseUrl is required.");
var filesKey = config["Files:ApiKey"];
if (string.IsNullOrEmpty(filesKey) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Files:ApiKey is required outside Development.");

builder.Services.AddSingleton(telegram);
builder.Services.AddSingleton(config.GetSection("Speech").Get<SpeechOptions>() ?? new SpeechOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new TelegramApi(telegram, sp.GetRequiredService<ILogger<TelegramApi>>()));
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
    c.Timeout = TimeSpan.FromSeconds(60); // the first voice note after a restart loads the model
});
builder.Services.AddHttpClient<FilesClient>(c =>
{
    c.BaseAddress = new Uri(filesUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(30);
    if (!string.IsNullOrEmpty(filesKey))
        c.DefaultRequestHeaders.Add("X-Api-Key", filesKey);
});
builder.Services.AddHostedService<TelegramBot>();

builder.Build().Run();
