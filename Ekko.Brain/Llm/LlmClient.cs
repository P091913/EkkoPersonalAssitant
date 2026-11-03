using System.Text.Json.Nodes;

namespace Ekko.Brain.Llm;

public sealed record LlmMessage(string Role, string Content);

/// <param name="Schema">The JSON schema the answer must follow. The client constrains output to it where the provider can.</param>
public sealed record LlmRequest(string System, IReadOnlyList<LlmMessage> Messages, JsonNode Schema);

/// <param name="Json">The model's answer as raw text - not yet validated.</param>
public sealed record LlmResult(string Json, string Model, int TokensIn, int TokensOut);

/// <summary>Transport or API failure: unreachable, timed out, refused, truncated. Not a validation failure.</summary>
public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One method for every model call in Ekko: routing and argument extraction both ask for JSON matching a schema.
/// Tool choice is part of that JSON ({tool, args}), not provider-native tool calling, so both providers share one path.
/// </summary>
public interface ILlmClient
{
    string Model { get; }

    Task<LlmResult> CompleteJsonAsync(LlmRequest request, CancellationToken ct);
}

public enum LlmKind { OpenAi, Anthropic }

public sealed class LlmClientOptions
{
    public LlmKind Kind { get; init; }
    public string BaseUrl { get; init; } = "";
    public string Model { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>OpenAI-compatible: reasoning_effort ("none" turns Qwen's thinking off). Anthropic: output_config.effort.</summary>
    public string? Effort { get; init; }
}

/// <summary>Which configured client handles a step, and which one it escalates to.</summary>
public sealed class StepModels
{
    public string Primary { get; init; } = "";
    public string? Fallback { get; init; }

    /// <summary>Used first in a deep session (DR-34), with <see cref="Primary"/> as its fallback. Unset: the step never switches.</summary>
    public string? Deep { get; init; }
}

public sealed class LlmOptions
{
    public Dictionary<string, LlmClientOptions> Clients { get; init; } = [];
    public Dictionary<string, StepModels> Steps { get; init; } = [];
}
