using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ekko.Telegram;

public sealed class TelegramOptions
{
    /// <summary>From @BotFather. A password: it's part of every Bot API URL.</summary>
    public string BotToken { get; init; } = "";

    /// <summary>Numeric Telegram user ids, comma-separated. Nobody else gets an answer.</summary>
    public string AllowedUsers { get; init; } = "";

    public HashSet<long> Allowed => AllowedUsers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(id => long.TryParse(id, out var n) ? n : 0).Where(n => n != 0).ToHashSet();
}

/// <summary>
/// The Telegram Bot API over raw HTTP. The token sits in every URL, so this client owns its HttpClient: the factory's
/// request logging would print each URL, token and all. Failures are logged by type only, never with the URL.
/// </summary>
public sealed class TelegramApi(TelegramOptions options, ILogger<TelegramApi> log, HttpMessageHandler? handler = null)
{
    private const string Api = "https://api.telegram.org";

    /// <summary>Telegram takes 4,096 characters; a little room is left for the cut mark.</summary>
    public const int MaxText = 4000;

    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null) { Timeout = Timeout.InfiniteTimeSpan };

    /// <returns>The result, or null. Status is Telegram's error code (401, 409) when it refused, 0 when it couldn't be reached.</returns>
    public async Task<(JsonNode? Result, int Status)> CallAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            using var response = await _http.PostAsJsonAsync($"{Api}/bot{options.BotToken}/{method}", parameters ?? new { }, limit.Token);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(limit.Token));
            if (body?["ok"]?.GetValue<bool>() == true)
                return (body["result"], 200);
            var description = body?["description"]?.ToString() ?? "";
            log.LogWarning("Telegram {Method} refused: {Status} {Description}", method, (int)response.StatusCode, description[..Math.Min(200, description.Length)]);
            return (null, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                   || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Telegram {Method} failed: {Error}", method, ex.GetType().Name);
            return (null, 0);
        }
    }

    public Task SendAsync(long chat, string text, CancellationToken ct) =>
        // Plain text on purpose: with a parse_mode, one stray * or _ makes Telegram reject the message.
        CallAsync("sendMessage", new { chat_id = chat, text = Cut(text) }, TimeSpan.FromSeconds(20), ct);

    public Task TypingAsync(long chat, CancellationToken ct) =>
        CallAsync("sendChatAction", new { chat_id = chat, action = "typing" }, TimeSpan.FromSeconds(5), ct);

    /// <summary>A file Telegram holds for the bot (a voice note), or null.</summary>
    public async Task<byte[]?> DownloadAsync(string fileId, CancellationToken ct)
    {
        var (file, _) = await CallAsync("getFile", new { file_id = fileId }, TimeSpan.FromSeconds(20), ct);
        if (file?["file_path"]?.ToString() is not { Length: > 0 } path)
            return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await _http.GetByteArrayAsync($"{Api}/file/bot{options.BotToken}/{path}", limit.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Telegram file download failed: {Error}", ex.GetType().Name);
            return null;
        }
    }

    public static string Cut(string text)
    {
        text = text.Trim();
        if (text.Length <= MaxText)
            return text.Length == 0 ? "..." : text;
        var cut = text[..(MaxText - 1)];
        return cut[..Math.Max(cut.LastIndexOf(' '), 1)] + "…";
    }
}
