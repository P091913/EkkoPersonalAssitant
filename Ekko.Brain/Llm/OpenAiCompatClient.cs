using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ekko.Brain.Llm;

/// <summary>Any OpenAI-compatible chat endpoint - here Ollama serving Qwen. Output is constrained with response_format json_schema.</summary>
public sealed class OpenAiCompatClient(HttpClient http, string model, string? reasoningEffort) : ILlmClient
{
    public string Model => model;

    public async Task<LlmResult> CompleteJsonAsync(LlmRequest request, CancellationToken ct)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = request.System } };
        foreach (var m in request.Messages)
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = 0,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "answer", ["strict"] = true, ["schema"] = request.Schema.DeepClone() },
            },
        };
        if (reasoningEffort is not null)
            body["reasoning_effort"] = reasoningEffort;

        var json = await LlmHttp.PostAsync(http, "chat/completions", body, request: null, ct);
        var choice = json.GetProperty("choices")[0];
        if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() == "length")
            throw new LlmException("output was cut off at the token limit");
        var content = choice.GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content))
            throw new LlmException("the model returned no content");

        var usage = json.TryGetProperty("usage", out var u) ? u : default;
        return new LlmResult(content, model, LlmHttp.Int(usage, "prompt_tokens"), LlmHttp.Int(usage, "completion_tokens"));
    }
}

internal static class LlmHttp
{
    /// <summary>POST JSON, return the parsed body; every transport/HTTP failure becomes an <see cref="LlmException"/>.</summary>
    public static async Task<JsonElement> PostAsync(HttpClient http, string path, JsonObject body, HttpRequestMessage? request,
        CancellationToken ct)
    {
        using var message = request ?? new HttpRequestMessage();
        message.Method = HttpMethod.Post;
        message.RequestUri = new Uri(path, UriKind.Relative);
        message.Content = JsonContent.Create(body);
        try
        {
            using var response = await http.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new LlmException($"HTTP {(int)response.StatusCode}: {Truncate(text)}");
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmException("timed out", ex);
        }
        catch (JsonException ex)
        {
            throw new LlmException("response wasn't JSON", ex);
        }
    }

    public static int Int(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
