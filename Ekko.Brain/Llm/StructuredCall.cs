using System.Diagnostics;
using System.Text.Json;
using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Llm;

/// <summary>
/// The policy for every model step, in code rather than in a prompt: ask the step's primary model, validate the answer,
/// retry once showing it the validation error, then escalate to the step's fallback model, then give up (null).
/// Every attempt is one trace line, so "how often does Qwen need a retry or Claude" is a query over the trace.
/// </summary>
public sealed class StructuredCall(IReadOnlyDictionary<string, ILlmClient> clients, IReadOnlyDictionary<string, StepModels> steps, TraceLog trace)
{
    /// <summary>Validate by deserializing into <typeparamref name="T"/> - the usual case.</summary>
    public static Func<JsonElement, (T? Value, string? Error)> Parse<T>() where T : class =>
        json => ToolJson.TryParse<T>(json, out var value, out var error) ? (value, null) : (null, error);

    /// <param name="deep">A long session (DR-34): the step's Deep model goes first and its usual Primary becomes the fallback,
    /// so Ekko still answers when Claude can't be reached. Without a Deep model, or its client (no API key), nothing changes.</param>
    public async Task<T?> AskAsync<T>(string step, LlmRequest request, Func<JsonElement, (T? Value, string? Error)> validate,
        TraceEvent t, CancellationToken ct, bool deep = false) where T : class
    {
        var models = steps.GetValueOrDefault(step) ?? throw new InvalidOperationException($"No Llm:Steps:{step} in config.");
        var (primaryName, fallbackName) = deep && models.Deep is { } d && clients.ContainsKey(d) ? (d, models.Primary) : (models.Primary, models.Fallback);
        var primary = clients.GetValueOrDefault(primaryName) ?? throw new InvalidOperationException($"Llm client '{primaryName}' isn't available.");
        if (deep)
            t = t with { Deep = true };

        var first = await TryAsync(step, primary, request, 1, validate, t, ct);
        if (first.Value is not null)
            return first.Value;

        // A transport failure won't improve by retrying the same model; a bad answer might, once it sees why.
        if (first.Output is not null)
        {
            var retry = request with
            {
                Messages = [.. request.Messages, new("assistant", first.Output),
                    new("user", $"That answer was invalid: {first.Error}. Reply again with only JSON that matches the schema.")],
            };
            var second = await TryAsync(step, primary, retry, 2, validate, t, ct);
            if (second.Value is not null)
                return second.Value;
        }

        // The fallback gets the original request: a clean context, not Qwen's mistakes.
        return fallbackName is { } name && clients.GetValueOrDefault(name) is { } fallback
            ? (await TryAsync(step, fallback, request, 3, validate, t, ct)).Value
            : null;
    }

    private sealed record Attempt<T>(T? Value, string? Output, string? Error);

    private async Task<Attempt<T>> TryAsync<T>(string step, ILlmClient client, LlmRequest request, int attempt,
        Func<JsonElement, (T? Value, string? Error)> validate, TraceEvent t, CancellationToken ct) where T : class
    {
        var started = Stopwatch.GetTimestamp();
        var line = t with { Step = $"llm:{step}", Model = client.Model, Attempt = attempt, Input = request.Messages[^1].Content };
        try
        {
            var result = await client.CompleteJsonAsync(request, ct);
            T? value;
            string? error;
            try
            {
                (value, error) = validate(JsonDocument.Parse(result.Json).RootElement.Clone());
            }
            catch (JsonException ex)
            {
                (value, error) = (null, $"not valid JSON ({ex.Message})");
            }
            error = value is null ? error ?? "invalid answer" : null;
            trace.Write(line with
            {
                Output = result.Json, Ok = value is not null, Error = error,
                TokensIn = result.TokensIn, TokensOut = result.TokensOut, LatencyMs = Ms(started),
            });
            return new(value, result.Json, error);
        }
        catch (LlmException ex)
        {
            trace.Write(line with { Ok = false, Error = ex.Message, LatencyMs = Ms(started) });
            return new(null, null, ex.Message);
        }
    }

    private static long Ms(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}

public static class LlmSetup
{
    /// <summary>One client per configured entry. An Anthropic entry without a key is left out, which disables escalation to it.</summary>
    public static Dictionary<string, ILlmClient> CreateClients(LlmOptions options, Func<string, HttpClient> httpFor, string? anthropicKey)
    {
        var clients = new Dictionary<string, ILlmClient>();
        foreach (var (name, o) in options.Clients)
        {
            if (o.Kind == LlmKind.Anthropic && string.IsNullOrWhiteSpace(anthropicKey))
                continue;
            clients[name] = o.Kind == LlmKind.Anthropic
                ? new AnthropicClient(httpFor(name), o.Model, anthropicKey!, o.Effort)
                : new OpenAiCompatClient(httpFor(name), o.Model, o.Effort);
        }
        return clients;
    }

    public static void ConfigureHttp(LlmClientOptions o, HttpClient http)
    {
        http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
    }
}
