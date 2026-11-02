using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ekko.Brain.Media;

public sealed class TvRemoteOptions
{
    public string BaseUrl { get; init; } = "";

    /// <summary>Long on purpose: playing can wake the TV and wait for Jellyfin to connect (tvremote allows ~95 s).</summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>Sent as X-Api-Key; tvremote checks it when its own Api__Key is set.</summary>
    public string? ApiKey { get; init; }
}

public sealed record MediaCandidate(string Id, string Name, string Type, int? ProductionYear, string Label);

/// <param name="Status">tvremote's contract: playing | ok | ambiguous | not_found | not_playing | unknown_command | error.</param>
/// <param name="Message">A sentence ready to read out.</param>
public sealed record TvAnswer(string Status, string Message, IReadOnlyList<MediaCandidate> Candidates);

/// <summary>
/// Client for tvremote, Ekko's only media executor. Ekko never searches Jellyfin itself: tvremote does the deterministic
/// title matching and answers 0/1/many, so ambiguity is settled in code and with the user, not by the model.
/// </summary>
public sealed class TvRemote(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record Response(string? Status, string? Message, ResponseData? Data);
    private sealed record ResponseData(List<MediaCandidate>? Candidates);

    public async Task<TvAnswer> PostAsync(string path, object? body, CancellationToken ct)
    {
        try
        {
            using var response = body is null ? await http.PostAsync(path, null, ct) : await http.PostAsJsonAsync(path, body, Json, ct);
            // tvremote answers errors (404, 400, 502) with the same JSON shape, so the body is read regardless of status.
            var parsed = await response.Content.ReadFromJsonAsync<Response>(Json, ct);
            return parsed is { Status: { } status, Message: { } message }
                ? new TvAnswer(status, message, parsed.Data?.Candidates ?? [])
                : new TvAnswer("error", "The TV remote gave an answer I couldn't read.", []);
        }
        catch (HttpRequestException)
        {
            return new TvAnswer("error", "The TV remote isn't answering.", []);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new TvAnswer("error", "The TV took too long to respond.", []);
        }
        catch (JsonException)
        {
            return new TvAnswer("error", "The TV remote gave an answer I couldn't read.", []);
        }
    }
}
