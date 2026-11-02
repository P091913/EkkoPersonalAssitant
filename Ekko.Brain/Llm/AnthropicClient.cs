using System.Text.Json.Nodes;

namespace Ekko.Brain.Llm;

/// <summary>Claude Messages API over raw HTTP. Output is constrained with output_config.format (structured outputs).</summary>
public sealed class AnthropicClient(HttpClient http, string model, string apiKey, string? effort) : ILlmClient
{
    // Structured outputs reject numeric and length constraints; our own validation still enforces them.
    private static readonly string[] Unsupported = ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength"];

    public string Model => model;

    public async Task<LlmResult> CompleteJsonAsync(LlmRequest request, CancellationToken ct)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        var outputConfig = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = Strip(request.Schema.DeepClone()) } };
        if (effort is not null)
            outputConfig["effort"] = effort;

        var body = new JsonObject
        {
            ["model"] = model,
            // Thinking is on by default for current models and counts toward this cap; leave room for it.
            ["max_tokens"] = 4096,
            ["system"] = request.System,
            ["messages"] = messages,
            ["output_config"] = outputConfig,
        };

        var headers = new HttpRequestMessage();
        headers.Headers.Add("x-api-key", apiKey);
        headers.Headers.Add("anthropic-version", "2026-10-21");
        var json = await LlmHttp.PostAsync(http, "messages", body, headers, ct);

        var stop = json.TryGetProperty("stop_reason", out var s) ? s.GetString() : null;
        if (stop is "refusal" or "max_tokens")
            throw new LlmException($"stopped with {stop}");
        // A response can start with thinking blocks: read by type, not position.
        var text = json.GetProperty("content").EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "text")
            .Select(b => b.GetProperty("text").GetString())
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(text))
            throw new LlmException("the model returned no text");

        var usage = json.GetProperty("usage");
        return new LlmResult(text, model, LlmHttp.Int(usage, "input_tokens"), LlmHttp.Int(usage, "output_tokens"));
    }

    private static JsonNode Strip(JsonNode node)
    {
        if (node is JsonObject schema)
        {
            foreach (var key in Unsupported)
                schema.Remove(key);
            foreach (var (key, child) in schema.ToList())
            {
                // "properties"/"$defs" map names to schemas: strip inside each, never the names themselves.
                if (key is "properties" or "$defs" && child is JsonObject map)
                    foreach (var (_, sub) in map)
                        Strip(sub!);
                else if (child is not null)
                    Strip(child);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                Strip(child!);
        }
        return node;
    }
}
