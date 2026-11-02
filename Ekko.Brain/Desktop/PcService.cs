using System.Net;
using System.Text.Json;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Desktop;

public sealed class PcServiceOptions
{
    public string BaseUrl { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 5;

    /// <summary>Sent as X-Api-Key to the PC agent.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The app allowlist: app name -> the PC agent's launch action id. Anything not here is refused.</summary>
    public Dictionary<string, string> Apps { get; init; } = [];
}

/// <summary>
/// Client for Ekko.PcAgent, the native Windows process that presses keys and launches apps (a container can't).
/// Its contract is one route, <c>POST /action/{id}</c>; which keys an id presses lives in the agent's config.
/// </summary>
public sealed class PcService(HttpClient http)
{
    public Task<ToolResult> PressAsync(string actionId, string said, CancellationToken ct) => PostAsync($"action/{actionId}", said, ct);

    private async Task<ToolResult> PostAsync(string path, string said, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsync(path, null, ct);
            if (response.IsSuccessStatusCode)
                return ToolResult.Done(said);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return ToolResult.Failed("The PC is busy with another action.");
            return ToolResult.Failed($"The PC couldn't do that: {ErrorOf(await response.Content.ReadAsStringAsync(ct)) ?? response.ReasonPhrase}.");
        }
        catch (HttpRequestException)
        {
            return ToolResult.Failed("The PC isn't answering.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Failed("The PC took too long to answer.");
        }
    }

    /// <summary>The service answers failures with {"ok":false,"error":"..."}; anything else falls back to the status text.</summary>
    private static string? ErrorOf(string body)
    {
        try
        {
            return JsonDocument.Parse(body).RootElement.GetProperty("error").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
