using System.Text.Json.Nodes;

namespace Ekko.Brain.Llm;

/// <param name="Content">The assistant's content blocks exactly as returned (text, thinking, tool_use). They are replayed unchanged.</param>
/// <param name="StopReason">end_turn, tool_use, max_tokens, refusal...</param>
public sealed record AgentTurn(JsonArray Content, string StopReason, string Model, int TokensIn, int TokensOut);

/// <summary>
/// Native tool calling: the model picks tools itself, reads their results and decides the next step. Only the
/// server-ops agent uses it (DR-4), and only Claude implements it; routing and extraction stay on <see cref="ILlmClient"/>.
/// </summary>
public interface IAgentModel
{
    string Model { get; }

    /// <param name="messages">The whole conversation so far, in the provider's message format. Append-only: never edited.</param>
    Task<AgentTurn> NextAsync(string system, JsonArray tools, JsonArray messages, CancellationToken ct);
}
