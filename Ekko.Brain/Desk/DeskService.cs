using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Desk;

public sealed class DeskOptions
{
    /// <summary>ekko-desk: <c>host.docker.internal:8081</c> while it runs on this PC, the Pi's tailnet URL after that.</summary>
    public string BaseUrl { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>Sent as X-Api-Key: the desk's DESK_BRAIN_KEY.</summary>
    public string? ApiKey { get; init; }
}

/// <summary>Client for ekko-desk (DR-39), which owns the notes and words its own answers, including its refusals.</summary>
public sealed class DeskService(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Response(bool Ok, string? Message, string? Error);

    public async Task<ToolResult> AddNoteAsync(string text, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/notes", new { text }, Json, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return ToolResult.Failed("Your desk refused Ekko's key.");
            // A full desk (409) or a bad note (400) comes back in the same shape, already worded for the user.
            return await response.Content.ReadFromJsonAsync<Response>(Json, ct) switch
            {
                { Ok: true, Message: { } message } => ToolResult.Done(message),
                { Ok: false, Error: { } error } => ToolResult.Failed(error),
                _ => Unreadable,
            };
        }
        catch (HttpRequestException)
        {
            return ToolResult.Failed("Your desk isn't answering.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Failed("Your desk took too long to answer.");
        }
        catch (JsonException)
        {
            return Unreadable;
        }
    }

    private static ToolResult Unreadable => ToolResult.Failed("Your desk gave an answer I couldn't read.");
}
