using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Ekko.Telegram.Tests;

/// <summary>Telegram, the Brain and speaches in one fake: answers by URL, and keeps every request.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public List<(string Url, string Body)> Requests { get; } = [];
    public Func<string, HttpResponseMessage> Respond { get; set; } = _ => Json("""{"ok":true,"result":{}}""");

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.RequestUri!.ToString(), body));
        return Respond(request.RequestUri.ToString());
    }

    public IEnumerable<string> Sent(string method) =>
        Requests.Where(r => r.Url.EndsWith("/" + method)).Select(r => JsonNode.Parse(r.Body)!["text"]!.ToString());
}

public sealed class Logs<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format) =>
        Lines.Add(format(state, ex) + (ex is null ? "" : " " + ex));
}

public sealed class Clock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

public class TelegramBotTests
{
    private const string Token = "123456:secret-token";
    private const long Me = 42;

    private readonly FakeHttp _http = new();
    private readonly Clock _clock = new();
    private readonly Logs<TelegramApi> _apiLogs = new();
    private readonly Logs<TelegramBot> _botLogs = new();
    private readonly TelegramBot _bot;

    public TelegramBotTests()
    {
        var options = new TelegramOptions { BotToken = Token, AllowedUsers = $" {Me} , 7" };
        _bot = new TelegramBot(new TelegramApi(options, _apiLogs, _http),
            new BrainClient(new HttpClient(_http) { BaseAddress = new Uri("http://brain/") }, new Logs<BrainClient>()),
            new Speech(new HttpClient(_http) { BaseAddress = new Uri("http://speech/") }, new SpeechOptions(), new Logs<Speech>()),
            options, _clock, _botLogs);
        _http.Respond = url => url switch
        {
            "http://brain/message" => FakeHttp.Json("""{"text":"Volume up.","requestId":"r1"}"""),
            "http://speech/v1/audio/transcriptions" => FakeHttp.Json("""{"text":" turn it up "}"""),
            _ when url.EndsWith("/getFile") => FakeHttp.Json("""{"ok":true,"result":{"file_path":"voice/file_1.oga"}}"""),
            _ when url.Contains("/file/bot") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) },
            _ => FakeHttp.Json("""{"ok":true,"result":{}}"""),
        };
    }

    private JsonObject Message(string? text = null, long from = Me, TimeSpan? ago = null, string? voice = null) => new()
    {
        ["message_id"] = 1,
        ["from"] = new JsonObject { ["id"] = from },
        ["chat"] = new JsonObject { ["id"] = from },
        ["date"] = (_clock.Now - (ago ?? TimeSpan.FromSeconds(2))).ToUnixTimeSeconds(),
        ["text"] = text,
        ["voice"] = voice is null ? null : JsonNode.Parse(voice),
    };

    private string BrainBody() => _http.Requests.Single(r => r.Url == "http://brain/message").Body;

    [Fact]
    public async Task A_text_goes_to_the_Brain_as_this_chats_conversation_and_the_answer_comes_back()
    {
        await _bot.HandleAsync(Message("volume up"), CancellationToken.None);

        Assert.Equal("""{"text":"volume up","channel":"telegram","conversationId":"tg:42"}""", BrainBody());
        Assert.Equal(["Volume up."], _http.Sent("sendMessage"));
    }

    [Fact]
    public async Task A_stranger_gets_no_answer_and_reaches_nothing()
    {
        await _bot.HandleAsync(Message("lock the pc", from: 99), CancellationToken.None);
        await _bot.HandleAsync(Message("lock the pc", from: 99), CancellationToken.None);

        Assert.Empty(_http.Requests);
        Assert.Single(_botLogs.Lines, l => l.Contains("99")); // logged once
    }

    [Fact]
    public async Task A_message_sent_while_the_PC_was_off_is_not_acted_on()
    {
        await _bot.HandleAsync(Message("lock the pc", ago: TimeSpan.FromMinutes(45)), CancellationToken.None);

        Assert.DoesNotContain(_http.Requests, r => r.Url.StartsWith("http://brain/"));
        Assert.Equal(["You sent this at 7:15 PM while I was off, so I didn't act on it. Say it again if you still want it."], _http.Sent("sendMessage"));
    }

    [Fact]
    public async Task A_voice_note_is_transcribed_then_answered_with_what_was_heard()
    {
        await _bot.HandleAsync(Message(voice: """{"file_id":"abc","duration":3}"""), CancellationToken.None);

        Assert.Contains(_http.Requests, r => r.Url == $"https://api.telegram.org/file/bot{Token}/voice/file_1.oga");
        Assert.Contains("\"text\":\"turn it up\"", BrainBody());
        Assert.Equal(["Heard: \"turn it up\"\n\nVolume up."], _http.Sent("sendMessage"));
    }

    [Fact]
    public async Task Speech_that_is_too_long_for_the_Brain_is_said_back_not_sent()
    {
        var talk = string.Join(' ', Enumerable.Repeat("blah", 120));
        _http.Respond = url => url.EndsWith("transcriptions") ? FakeHttp.Json(JsonSerializer.Serialize(new { text = talk }))
            : url.EndsWith("/getFile") ? FakeHttp.Json("""{"ok":true,"result":{"file_path":"v.oga"}}""")
            : FakeHttp.Json("""{"ok":true,"result":{}}""");

        await _bot.HandleAsync(Message(voice: """{"file_id":"abc","duration":40}"""), CancellationToken.None);

        Assert.DoesNotContain(_http.Requests, r => r.Url.StartsWith("http://brain/"));
        Assert.EndsWith("That's longer than Ekko takes in one message. Try it in shorter pieces.", _http.Sent("sendMessage").Single());
    }

    [Fact]
    public async Task A_refused_key_is_said_plainly()
    {
        _http.Respond = url => url.StartsWith("http://brain/") ? FakeHttp.Json("""{"error":"missing or wrong X-Api-Key"}""", HttpStatusCode.Unauthorized)
            : FakeHttp.Json("""{"ok":true,"result":{}}""");

        await _bot.HandleAsync(Message("volume up"), CancellationToken.None);

        Assert.Equal(["Ekko refused this bot's key, so I couldn't pass that on."], _http.Sent("sendMessage"));
    }
}

public class TelegramApiTests
{
    private const string Token = "123456:secret-token";

    [Fact]
    public async Task The_token_never_reaches_the_logs()
    {
        var http = new FakeHttp();
        var logs = new Logs<TelegramApi>();
        var api = new TelegramApi(new TelegramOptions { BotToken = Token }, logs, http);

        http.Respond = url => throw new HttpRequestException($"failed: {url}");
        Assert.Equal((null, 0), await api.CallAsync("getMe", null, TimeSpan.FromSeconds(5), CancellationToken.None));
        http.Respond = _ => FakeHttp.Json("""{"ok":false,"error_code":409,"description":"Conflict: terminated by other getUpdates request"}""", HttpStatusCode.Conflict);
        Assert.Equal(409, (await api.CallAsync("getUpdates", null, TimeSpan.FromSeconds(5), CancellationToken.None)).Status);
        Assert.Null(await api.DownloadAsync("abc", CancellationToken.None));

        Assert.Equal(3, logs.Lines.Count);
        Assert.DoesNotContain(logs.Lines, l => l.Contains("secret-token"));
    }

    [Fact]
    public void Long_replies_are_cut_at_a_word_under_Telegrams_limit()
    {
        var cut = TelegramApi.Cut(string.Join(' ', Enumerable.Repeat("word", 1200)));
        Assert.True(cut.Length <= TelegramApi.MaxText);
        Assert.EndsWith("word…", cut);
        Assert.Equal("...", TelegramApi.Cut("  "));
    }
}
