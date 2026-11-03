using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ekko.Brain.Food;

public sealed class FoodOptions
{
    public string BaseUrl { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>Sent as X-Api-Key; the food service has a key per client.</summary>
    public string? ApiKey { get; init; }
}

/// <param name="Status">ok | found | unknown | empty | error.</param>
/// <param name="Message">A sentence ready to read out.</param>
/// <param name="Unknown">With "unknown": the foods not on the list, exactly as Ekko sent them.</param>
/// <param name="Name">With "found": the food as the list names it, and its calories for one.</param>
public sealed record FoodAnswer(string Status, string Message, IReadOnlyList<string> Unknown, string? Name = null, double? CaloriesEach = null);

/// <summary>Client for Ekko.Food, which owns the food list and diary. It matches foods in code and words every answer.</summary>
public sealed class FoodService(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record Response(string? Status, string? Message, List<string>? Unknown, string? Name, double? CaloriesEach);

    public Task<FoodAnswer> GetAsync(string path, CancellationToken ct) => SendAsync(() => http.GetAsync(path, ct), ct);

    public Task<FoodAnswer> PostAsync(string path, object? body, CancellationToken ct) => SendAsync(() => http.PostAsJsonAsync(path, body, Json, ct), ct);

    private static async Task<FoodAnswer> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            using var response = await send();
            // Errors (400) come back in the same shape, so the body is read regardless of status.
            var parsed = await response.Content.ReadFromJsonAsync<Response>(Json, ct);
            return parsed is { Status: { } status, Message: { } message }
                ? new FoodAnswer(status, message, parsed.Unknown ?? [], parsed.Name, parsed.CaloriesEach)
                : new FoodAnswer("error", "The food service gave an answer I couldn't read.", []);
        }
        catch (HttpRequestException)
        {
            return new FoodAnswer("error", "The food service isn't answering.", []);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FoodAnswer("error", "The food service took too long to answer.", []);
        }
        catch (JsonException)
        {
            return new FoodAnswer("error", "The food service gave an answer I couldn't read.", []);
        }
    }
}
