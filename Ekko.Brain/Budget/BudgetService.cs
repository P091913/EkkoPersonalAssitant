using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ekko.Brain.Budget;

public sealed class BudgetOptions
{
    public string BaseUrl { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 20;

    /// <summary>Sent as X-Api-Key; the budget service has a key per client.</summary>
    public string? ApiKey { get; init; }
}

/// <summary>A sheet row a request might mean. Tab, Row and Item go back unchanged to check it off.</summary>
public sealed record BudgetCandidate(string Tab, int Row, string Item, decimal Amount, string Label);

/// <param name="Status">ok | found | ambiguous | not_found | already | conflict | error.</param>
/// <param name="Message">A sentence ready to read out.</param>
public sealed record BudgetAnswer(string Status, string Message, IReadOnlyList<BudgetCandidate> Candidates);

/// <summary>
/// Client for Ekko.Budget, the only thing that touches the expenses sheet. Ekko never reads the sheet itself: the service
/// matches "the water bill" to a row in code and words every answer.
/// </summary>
public sealed class BudgetService(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record Response(string? Status, string? Message, List<BudgetCandidate>? Candidates);

    public Task<BudgetAnswer> GetAsync(string path, CancellationToken ct) => SendAsync(() => http.GetAsync(path, ct), ct);

    public Task<BudgetAnswer> PostAsync(string path, object body, CancellationToken ct) => SendAsync(() => http.PostAsJsonAsync(path, body, Json, ct), ct);

    private static async Task<BudgetAnswer> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            using var response = await send();
            // Errors (400, 502) come back in the same shape, so the body is read regardless of status.
            var parsed = await response.Content.ReadFromJsonAsync<Response>(Json, ct);
            return parsed is { Status: { } status, Message: { } message }
                ? new BudgetAnswer(status, message, parsed.Candidates ?? [])
                : new BudgetAnswer("error", "The budget service gave an answer I couldn't read.", []);
        }
        catch (HttpRequestException)
        {
            return new BudgetAnswer("error", "The budget service isn't answering.", []);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new BudgetAnswer("error", "The budget service took too long to answer.", []);
        }
        catch (JsonException)
        {
            return new BudgetAnswer("error", "The budget service gave an answer I couldn't read.", []);
        }
    }
}
