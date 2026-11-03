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

    /// <summary>Only the two routes that reach Ekko need the key; the page itself holds no secrets.</summary>
    public static bool NeedsKey(PathString path) => path.StartsWithSegments("/hear") || path.StartsWithSegments("/ask");

    /// <summary>Compared in constant time. No configured key means open: Development only, Program refuses otherwise.</summary>
    public static bool KeyMatches(string? configured, string? supplied) =>
        string.IsNullOrEmpty(configured)
        || !string.IsNullOrEmpty(supplied) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(configured), Encoding.UTF8.GetBytes(supplied));
}

public sealed record AskRequest(string? Text);

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
