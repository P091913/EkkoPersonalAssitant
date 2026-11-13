using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ekko.Glasses;

/// <summary>What a caller may send, checked at the edge.</summary>
public static class Talk
{
    /// <summary>About a minute of the glasses' 16 kHz mic as WAV; far more of a phone's compressed audio.</summary>
    public const long MaxAudioBytes = 2 * 1024 * 1024;

    /// <summary>The audio speaches is sent, by what was recorded: WAV from the glasses, a browser's own format in preview.</summary>
    public static string? FileName(string? contentType) => contentType?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "audio/wav" or "audio/x-wav" or "audio/wave" => "talk.wav",
        "audio/webm" => "talk.webm",
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" => "talk.m4a",
        "audio/ogg" => "talk.ogg",
        _ => null,
    };

    public static string? Problem(AskRequest r) =>
        string.IsNullOrWhiteSpace(r.Text) ? "text is required."
        : r.Text.Trim().Length > BrainClient.MaxText ? $"text is limited to {BrainClient.MaxText} characters."
        : null;

    /// <summary>Only the routes that reach Ekko need the key; the page itself holds no secrets.</summary>
    public static bool NeedsKey(PathString path) =>
        path.StartsWithSegments("/hear") || path.StartsWithSegments("/ask") || path.StartsWithSegments("/v1");

    /// <summary>The page sends X-Api-Key; OpenAI-style callers, like the Even app's agent setting (DR-49), send a Bearer token.</summary>
    public static string? SuppliedKey(IHeaderDictionary headers) =>
        headers["X-Api-Key"] is { Count: > 0 } key ? key.ToString()
        : headers.Authorization.ToString() is var auth && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim()
        : null;

    /// <summary>Compared in constant time. No configured key means open: Development only, Program refuses otherwise.</summary>
    public static bool KeyMatches(string? configured, string? supplied) =>
        string.IsNullOrEmpty(configured)
        || !string.IsNullOrEmpty(supplied) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(configured), Encoding.UTF8.GetBytes(supplied));
}

public sealed record AskRequest(string? Text);

/// <summary>An OpenAI-style chat request, as the Even app's agent setting sends it (DR-49): only what Ekko reads.</summary>
public sealed record ChatRequest(List<ChatMessage>? Messages, bool Stream = false);

/// <summary>Content is a string, or a list of parts of which only the text ones count.</summary>
public sealed record ChatMessage(string? Role, JsonElement Content);

/// <summary>OpenAI's chat format, which the Even app's agent setting speaks (DR-49).</summary>
public static class OpenAiChat
{
    /// <summary>
    /// The newest thing the user said. The caller sends the whole chat every time, but Ekko keeps its own session (DR-33),
    /// so the earlier messages would only repeat what it already has.
    /// </summary>
    public static string? LastUserText(ChatRequest r) => r.Messages?.LastOrDefault(m => m.Role == "user")?.Content switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString(),
        { ValueKind: JsonValueKind.Array } parts => string.Join(" ", parts.EnumerateArray()
            .Select(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("type", out var type) && type.ValueEquals("text")
                         && p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)
            .OfType<string>()),
        _ => null,
    };

    public static object Completion(string reply) => new
    {
        id = Id(), @object = "chat.completion", created = Now(), model = "ekko",
        choices = new[] { new { index = 0, message = new { role = "assistant", content = reply }, finish_reason = "stop" } },
    };

    /// <summary>The same reply as server-sent events, for a caller that asked to stream: all of it in one chunk, then the end.</summary>
    public static string Stream(string reply)
    {
        var (id, created) = (Id(), Now());
        string Chunk(object delta, string? finish) => "data: " + JsonSerializer.Serialize(new
        {
            id, @object = "chat.completion.chunk", created, model = "ekko",
            choices = new[] { new { index = 0, delta, finish_reason = finish } },
        }) + "\n\n";
        return Chunk(new { role = "assistant", content = reply }, null) + Chunk(new { }, "stop") + "data: [DONE]\n\n";
    }

    private static string Id() => "ekko-" + Guid.NewGuid().ToString("N");

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

/// <summary>The Brain, as the deck and Telegram reach it: POST /message with this service's own key (DR-38).</summary>
public sealed class BrainClient(HttpClient http, ILogger<BrainClient> log)
{
    /// <summary>The Brain's own limit on one message.</summary>
    public const int MaxText = 500;

    private sealed record Reply(string? Text, string? Error);

    /// <summary>Ekko's answer, or a sentence saying why there isn't one. One conversation for the glasses, so follow-ups work.</summary>
    public async Task<string> AskAsync(string text, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("message", new { text, channel = "glasses", conversationId = "glasses" }, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                log.LogError("The Brain refused this service's key: check Api__Keys__glasses in .env");
                return "Ekko refused the glasses' key, so I couldn't pass that on.";
            }
            var reply = await response.Content.ReadFromJsonAsync<Reply>(ct);
            return reply?.Text is { Length: > 0 } said ? said
                : reply?.Error is { Length: > 0 } error ? $"Ekko couldn't take that: {error}."
                : "Ekko gave an answer I couldn't read.";
        }
        catch (HttpRequestException)
        {
            return "Ekko isn't answering right now.";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Ekko took too long to answer.";
        }
        catch (JsonException)
        {
            return "Ekko gave an answer I couldn't read.";
        }
    }
}

public sealed class SpeechOptions
{
    public string Model { get; init; } = "Systran/faster-distil-whisper-small.en";
}

/// <summary>speaches, the speech server on this PC (OpenAI-compatible). It reads WAV, WebM, MP4 and Ogg audio as sent.</summary>
public sealed class Speech(HttpClient http, SpeechOptions options, ILogger<Speech> log)
{
    private sealed record Heard(string? Text);

    /// <returns>What was heard ("" for silence), or null when speaches couldn't be asked.</returns>
    public async Task<string?> TranscribeAsync(byte[] audio, string contentType, string fileName, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new(contentType.Split(';')[0].Trim());
        form.Add(file, "file", fileName);
        form.Add(new StringContent(options.Model), "model");
        form.Add(new StringContent("en"), "language");
        form.Add(new StringContent("json"), "response_format");
        try
        {
            using var response = await http.PostAsync("v1/audio/transcriptions", form, ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("speaches answered {Status}", (int)response.StatusCode);
                return null;
            }
            return (await response.Content.ReadFromJsonAsync<Heard>(ct))?.Text?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("speaches couldn't be asked: {Error}", ex.GetType().Name);
            return null;
        }
    }
}
