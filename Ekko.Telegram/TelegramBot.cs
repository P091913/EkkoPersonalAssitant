using System.Globalization;
using System.Text.Json.Nodes;

namespace Ekko.Telegram;

/// <summary>
/// Text and voice notes from your phone to Ekko, and its answer back (DR-37). The bot long-polls Telegram, so every
/// connection is outgoing and nothing here is opened to the internet. Only user ids in Telegram:AllowedUsers get an
/// answer; Telegram says who sent each message, so that can't be faked.
/// </summary>
public sealed class TelegramBot(TelegramApi telegram, BrainClient brain, Speech speech, FilesClient files, TelegramOptions options,
    TimeProvider clock, ILogger<TelegramBot> log) : BackgroundService
{
    private const int PollSeconds = 30;
    private const int MaxVoiceSeconds = 120;
    /// <summary>The most a bot may download from Telegram.</summary>
    private const long MaxFileBytes = 20 * 1024 * 1024;

    /// <summary>A message older than this was sent while the PC was off. Acting on it now could surprise ("lock the pc").</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private readonly HashSet<long> _strangers = [];
    private readonly HashSet<long> _allowed = options.Allowed;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // A refused token stops the bot; an unreachable Telegram is waited out.
        for (var attempt = 0; ; attempt++)
        {
            var (me, status) = await telegram.CallAsync("getMe", null, TimeSpan.FromSeconds(10), stop);
            if (me is not null)
            {
                log.LogInformation("Telegram on: @{Bot}, answering {Count} user(s)", me["username"]?.ToString(), _allowed.Count);
                break;
            }
            if (status is 401 or 404)
            {
                log.LogError("Telegram refused the bot token (Telegram_BotToken in .env): the bot is off");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 << Math.Min(attempt, 4))), stop);
        }

        long offset = 0;
        var conflicted = false;
        while (!stop.IsCancellationRequested)
        {
            var (updates, status) = await telegram.CallAsync("getUpdates",
                new { offset, timeout = PollSeconds, allowed_updates = new[] { "message" } }, TimeSpan.FromSeconds(PollSeconds + 10), stop);
            if (updates is not JsonArray list)
            {
                if (status == 401)
                {
                    log.LogError("Telegram refused the bot token (Telegram_BotToken in .env): the bot is off");
                    return;
                }
                // Only one program can receive a bot's messages, and two take each other's.
                if (status == 409 && !conflicted)
                    log.LogWarning("Something else is receiving this bot's messages (the Pi's pc-waker.service, if it's running again); retrying");
                conflicted = status == 409;
                await Task.Delay(TimeSpan.FromSeconds(conflicted ? 15 : 5), stop);
                continue;
            }
            conflicted = false;
            foreach (var update in list.OfType<JsonObject>())
            {
                offset = (update["update_id"]?.GetValue<long>() ?? offset) + 1;
                if (update["message"] is not JsonObject message)
                    continue;
                try
                {
                    await HandleAsync(message, stop);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError("A Telegram message failed: {Error}", ex.GetType().Name);
                }
            }
        }
    }

    public async Task HandleAsync(JsonObject message, CancellationToken ct)
    {
        var user = message["from"]?["id"]?.GetValue<long>() ?? 0;
        var chat = message["chat"]?["id"]?.GetValue<long>() ?? 0;
        if (user == 0 || chat == 0)
            return;
        if (!_allowed.Contains(user))
        {
            // No reply: a reply tells a stranger the bot is live. Logged once, which is also how you'd find your own id.
            if (_strangers.Add(user))
                log.LogWarning("Ignored Telegram user {Id}: not in Telegram_AllowedUsers", user);
            return;
        }

        var sent = DateTimeOffset.FromUnixTimeSeconds(message["date"]?.GetValue<long>() ?? 0);
        if (clock.GetUtcNow() - sent > Stale)
        {
            var at = TimeZoneInfo.ConvertTime(sent, clock.LocalTimeZone).ToString("h:mm tt", En);
            await telegram.SendAsync(chat, $"You sent this at {at} while I was off, so I didn't act on it. Say it again if you still want it.", ct);
            return;
        }

        // A photo or document carries its words as a caption.
        var text = (message["text"] ?? message["caption"])?.ToString().Trim() ?? "";
        if (text == "/start")
        {
            await telegram.SendAsync(chat, "Hi! Text me or send a voice note, the way you'd talk to Ekko on the deck.", ct);
            return;
        }
        await telegram.TypingAsync(chat, ct);
        var reply = message["voice"] is JsonObject voice ? await VoiceAsync(voice, chat, ct)
            : Attachment(message) is { } file ? await FileAsync(file, text, chat, ct)
            : text.Length == 0 ? "I can read text and voice notes, and keep photos and PDFs."
            : await AskAsync(text, chat, ct);
        await telegram.SendAsync(chat, reply, ct);
    }

    private Task<string> AskAsync(string text, long chat, CancellationToken ct) =>
        text.Length > BrainClient.MaxText
            ? Task.FromResult($"That's longer than Ekko takes in one message ({BrainClient.MaxText} characters). Try it shorter.")
            : brain.AskAsync(text, chat, ct);

    /// <summary>A photo (its largest size; Telegram sends them as JPEG with no name) or a document.</summary>
    private static (string Id, string Name, long Size)? Attachment(JsonObject message) =>
        message["photo"] is JsonArray { Count: > 0 } sizes && sizes[sizes.Count - 1] is JsonObject biggest
            ? (biggest["file_id"]?.ToString() ?? "", "photo.jpg", biggest["file_size"]?.GetValue<long>() ?? 0)
            : message["document"] is JsonObject document
                ? (document["file_id"]?.ToString() ?? "", document["file_name"]?.ToString() ?? "file", document["file_size"]?.GetValue<long>() ?? 0)
                : null;

    /// <summary>Kept in Ekko.Files, which decides what it takes. A caption goes to Ekko only once the file is kept: it's likely about the file.</summary>
    private async Task<string> FileAsync((string Id, string Name, long Size) file, string caption, long chat, CancellationToken ct)
    {
        if (file.Size > MaxFileBytes)
            return "That file is over 20 MB, more than Telegram lets me download.";
        if (file.Id.Length == 0 || await telegram.DownloadAsync(file.Id, ct) is not { } bytes)
            return "Sorry, I couldn't download that file.";
        var (kept, said) = await files.SaveAsync(bytes, file.Name, ct);
        return !kept || caption.Length == 0 ? said : $"{said}\n\n{await AskAsync(caption, chat, ct)}";
    }

    private async Task<string> VoiceAsync(JsonObject voice, long chat, CancellationToken ct)
    {
        if ((voice["duration"]?.GetValue<int>() ?? 0) > MaxVoiceSeconds)
            return "That voice note is over 2 minutes. Try a shorter one.";
        if (voice["file_id"]?.ToString() is not { Length: > 0 } id || await telegram.DownloadAsync(id, ct) is not { } audio)
            return "Sorry, I couldn't download that voice note.";
        var heard = await speech.TranscribeAsync(audio, ct);
        if (heard is null)
            return "Sorry, I couldn't transcribe that voice note right now.";
        if (heard.Length == 0)
            return "I couldn't hear anything in that voice note.";
        if (heard.Length > BrainClient.MaxText)
            return $"Heard: \"{heard}\"\n\nThat's longer than Ekko takes in one message. Try it in shorter pieces.";
        return $"Heard: \"{heard}\"\n\n{await brain.AskAsync(heard, chat, ct)}";
    }
}
