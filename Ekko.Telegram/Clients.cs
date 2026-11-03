using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ekko.Telegram;

/// <summary>The Brain, as the deck reaches it: POST /message with this bot's own key (DR-37).</summary>
public sealed class BrainClient(HttpClient http, ILogger<BrainClient> log)
{
    /// <summary>The Brain's own limit on one message.</summary>
    public const int MaxText = 500;

    private sealed record Reply(string? Text, string? Error);

    /// <summary>Ekko's answer, or a sentence saying why there isn't one. One conversation per chat, so follow-ups work.</summary>
    public async Task<string> AskAsync(string text, long chat, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("message", new { text, channel = "telegram", conversationId = $"tg:{chat}" }, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                log.LogError("The Brain refused this bot's key: check Api__Keys__telegram in .env");
                return "Ekko refused this bot's key, so I couldn't pass that on.";
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

/// <summary>speaches, the speech server on this PC (OpenAI-compatible). Voice notes are Ogg/Opus, which Whisper reads as is.</summary>
public sealed class Speech(HttpClient http, SpeechOptions options, ILogger<Speech> log)
{
    private sealed record Heard(string? Text);

    /// <returns>What was heard ("" for silence), or null when speaches couldn't be asked.</returns>
    public async Task<string?> TranscribeAsync(byte[] audio, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new("audio/ogg");
        form.Add(file, "file", "voice.ogg");
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
