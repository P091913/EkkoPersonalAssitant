using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ekko.Glasses.Tests;

/// <summary>The Brain and speaches in one fake: answers by URL, and keeps every request.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public List<(string Url, string? ContentType, string Body)> Requests { get; } = [];
    public Func<string, HttpResponseMessage> Respond { get; set; } = _ => Json("{}");

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.RequestUri!.ToString(), request.Content?.Headers.ContentType?.MediaType,
            request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
        return Respond(request.RequestUri.ToString());
    }
}

public class TalkTests
{
    [Theory]
    [InlineData("audio/wav", "talk.wav")]
    [InlineData("audio/webm;codecs=opus", "talk.webm")]
    [InlineData("audio/mp4", "talk.m4a")]
    [InlineData("Audio/Ogg; codecs=opus", "talk.ogg")]
    [InlineData("application/json", null)]
    [InlineData("text/plain", null)]
    [InlineData(null, null)]
    public void Only_audio_reaches_speaches(string? contentType, string? fileName) => Assert.Equal(fileName, Talk.FileName(contentType));

    [Theory]
    [InlineData("/hear", true)]
    [InlineData("/ask", true)]
    [InlineData("/ASK", true)]
    [InlineData("/v1/chat/completions", true)]
    [InlineData("/", false)]
    [InlineData("/app.js", false)]
    [InlineData("/asking.js", false)]
    public void Only_the_routes_that_reach_Ekko_need_the_key(string path, bool needs) => Assert.Equal(needs, Talk.NeedsKey(new PathString(path)));

    [Theory]
    [InlineData("page-secret", "page-secret", true)]
    [InlineData("page-secret", "Page-secret", false)]
    [InlineData("page-secret", "", false)]
    [InlineData("page-secret", null, false)]
    [InlineData(null, null, true)] // no key configured: open, Development only (Program refuses to start otherwise)
    public void The_page_key_must_match_exactly(string? configured, string? supplied, bool ok) =>
        Assert.Equal(ok, Talk.KeyMatches(configured, supplied));

    [Theory]
    [InlineData("X-Api-Key", "page-secret", "page-secret")]
    [InlineData("Authorization", "Bearer page-secret", "page-secret")]
    [InlineData("Authorization", "bearer  page-secret ", "page-secret")]
    [InlineData("Authorization", "Basic page-secret", null)]
    [InlineData("Accept", "page-secret", null)]
    public void The_key_comes_from_X_Api_Key_or_a_Bearer_token(string header, string value, string? key) =>
        Assert.Equal(key, Talk.SuppliedKey(new HeaderDictionary { [header] = value }));

    [Theory]
    [InlineData(null, "text is required.")]
    [InlineData("   ", "text is required.")]
    [InlineData("volume up", null)]
    public void What_is_asked_is_checked(string? text, string? problem) => Assert.Equal(problem, Talk.Problem(new AskRequest(text)));

    [Fact]
    public void Text_over_the_Brains_limit_is_refused_here() =>
        Assert.Equal("text is limited to 500 characters.", Talk.Problem(new AskRequest(new string('a', 501))));
}

public class OpenAiChatTests
{
    private static ChatRequest Parse(string json) =>
        JsonSerializer.Deserialize<ChatRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Theory]
    [InlineData("""{"messages":[{"role":"system","content":"be brief"},{"role":"user","content":"volume up"}]}""", "volume up")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"Hi."},{"role":"user","content":"volume up"}]}""", "volume up")]
    [InlineData("""{"messages":[{"role":"user","content":"volume up"},{"role":"assistant","content":"Volume up."}]}""", "volume up")]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"text","text":"volume"},{"type":"image_url","image_url":{}},{"type":"text","text":"up"}]}]}""", "volume up")]
    [InlineData("""{"messages":[{"role":"system","content":"be brief"}]}""", null)]
    [InlineData("""{"messages":[{"role":"user","content":null}]}""", null)]
    [InlineData("""{}""", null)]
    public void Only_the_newest_user_words_go_to_Ekko(string json, string? text) => Assert.Equal(text, OpenAiChat.LastUserText(Parse(json)));

    [Fact]
    public void Stream_defaults_to_off() => Assert.False(Parse("""{"messages":[]}""").Stream);

    [Fact]
    public void The_reply_is_a_chat_completion()
    {
        var json = JsonSerializer.SerializeToElement(OpenAiChat.Completion("Volume up."), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("chat.completion", json.GetProperty("object").GetString());
        var choice = json.GetProperty("choices")[0];
        Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("Volume up.", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void A_streamed_reply_is_one_chunk_then_the_end()
    {
        var events = OpenAiChat.Stream("Volume up.").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, events.Length);
        Assert.All(events, e => Assert.StartsWith("data: ", e));
        Assert.Equal("data: [DONE]", events[2]);
        var first = JsonDocument.Parse(events[0][6..]).RootElement.GetProperty("choices")[0];
        Assert.Equal("Volume up.", first.GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("finish_reason").ValueKind);
        Assert.Equal("stop", JsonDocument.Parse(events[1][6..]).RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }
}

public class ClientTests
{
    private readonly FakeHttp _http = new();

    private BrainClient Brain() => new(new HttpClient(_http) { BaseAddress = new Uri("http://brain/") }, NullLogger<BrainClient>.Instance);

    private Speech Speech() => new(new HttpClient(_http) { BaseAddress = new Uri("http://speech/") }, new SpeechOptions(), NullLogger<Speech>.Instance);

    [Fact]
    public async Task What_was_said_goes_to_the_Brain_as_the_glasses_conversation()
    {
        _http.Respond = _ => FakeHttp.Json("""{"text":"Volume up.","requestId":"r1"}""");
        Assert.Equal("Volume up.", await Brain().AskAsync("volume up", CancellationToken.None));
        Assert.Equal("""{"text":"volume up","channel":"glasses","conversationId":"glasses"}""", _http.Requests.Single().Body);
    }

    [Fact]
    public async Task A_refused_key_or_a_down_Brain_is_said_plainly()
    {
        _http.Respond = _ => FakeHttp.Json("""{"error":"missing or wrong X-Api-Key"}""", HttpStatusCode.Unauthorized);
        Assert.Equal("Ekko refused the glasses' key, so I couldn't pass that on.", await Brain().AskAsync("volume up", CancellationToken.None));
        _http.Respond = _ => throw new HttpRequestException("connection refused");
        Assert.Equal("Ekko isn't answering right now.", await Brain().AskAsync("volume up", CancellationToken.None));
    }

    [Fact]
    public async Task Audio_goes_to_speaches_with_its_own_type_and_the_text_comes_back_trimmed()
    {
        _http.Respond = _ => FakeHttp.Json("""{"text":" Turn the volume up. "}""");
        Assert.Equal("Turn the volume up.", await Speech().TranscribeAsync([1, 2, 3], "audio/webm;codecs=opus", "talk.webm", CancellationToken.None));

        var sent = _http.Requests.Single();
        Assert.Equal("http://speech/v1/audio/transcriptions", sent.Url);
        Assert.Contains("filename=talk.webm", sent.Body);
        Assert.Contains("Content-Type: audio/webm", sent.Body);
        Assert.Contains("Systran/faster-distil-whisper-small.en", sent.Body);
    }

    [Fact]
    public async Task When_speaches_fails_there_is_no_text()
    {
        _http.Respond = _ => FakeHttp.Json("""{"detail":"boom"}""", HttpStatusCode.InternalServerError);
        Assert.Null(await Speech().TranscribeAsync([1, 2, 3], "audio/wav", "talk.wav", CancellationToken.None));
    }
}
