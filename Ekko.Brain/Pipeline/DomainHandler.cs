using System.Text.Json;
using System.Text.Json.Nodes;
using Ekko.Brain.Llm;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Pipeline;

public sealed record ToolCall(string Tool, JsonElement Args)
{
    /// <summary>The model's way to say "none of these tools does that".</summary>
    public const string None = "none";
}

/// <summary>
/// A domain is a short tool list plus one extraction call. The model sees only that domain's tools and must answer
/// one of them or "none": an explicit way out beats forcing it to pick the closest wrong tool ("close Discord" -> open_app).
/// </summary>
public sealed class DomainHandler(ToolCatalog catalog, StructuredCall llm)
{
    public Task<ToolCall?> ExtractAsync(Domain domain, string text, History history, TraceEvent t, CancellationToken ct)
    {
        var tools = catalog.ForDomain(domain);
        // The note on earlier turns only when there are some: a single message gets exactly the prompt the evals were tuned on.
        var prompt = Prompt(domain, tools) + (history.Turns.Count > 0 ? HistoryNote : "");
        var request = new LlmRequest(prompt, history.Messages(text), Schema(tools));
        return llm.AskAsync("extract", request, json => Validate(json, tools), t, ct, history.Deep);
    }

    private const string HistoryNote = """

        Earlier turns can fill in what the latest message leaves out: after "Add lunch, $12 spent, marked unexpected. Go ahead?",
        "no, it was planned" is that same expense with planned true. The call is for the latest message.
        """;

    /// <summary>anyOf one {tool: const name, args: that tool's schema} per tool, plus "none" - so constrained decoding can't mix them up.</summary>
    public static JsonNode Schema(IReadOnlyList<ITool> tools) =>
        new JsonObject { ["anyOf"] = new JsonArray([.. tools.Select(t => Option(t.Name, t.Schema)), Option(ToolCall.None, ToolJson.SchemaFor<NoArgs>())]) };

    private static JsonObject Option(string name, JsonNode args) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["tool"] = new JsonObject { ["const"] = name }, ["args"] = args.DeepClone() },
        ["required"] = new JsonArray("tool", "args"),
        ["additionalProperties"] = false,
    };

    // Ollama uses the schema only to constrain decoding - the model never reads it - so the prompt spells the tools out.
    private static string Prompt(Domain domain, IReadOnlyList<ITool> tools) => $$$"""
        You turn one voice command into a single tool call for Ekko's {{{Router.Name(domain)}}} domain.
        Tools:
        {{{string.Join("\n", tools.Select(t => $"- {t.Name}: {t.Description} Args: {t.Schema.ToJsonString()}"))}}}
        Answer {"tool": "<name>", "args": {...}}. Leave out optional args the user didn't mention.
        If none of these tools does what was asked, answer {"tool": "none", "args": {}}. Never pick a tool that does something different.
        """;

    /// <summary>Shape only (<see cref="ITool.Validate"/>). Allowlists are the gate's job: a correct but disallowed call is not a model error.</summary>
    private static (ToolCall? Value, string? Error) Validate(JsonElement json, IReadOnlyList<ITool> tools)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("tool", out var name) || name.ValueKind != JsonValueKind.String
            || !json.TryGetProperty("args", out var args))
            return (null, """expected {"tool": "<name>", "args": {...}}""");

        var toolName = name.GetString()!;
        if (toolName == ToolCall.None)
            return (new ToolCall(ToolCall.None, args.Clone()), null);
        // Only this domain's tools: a router mistake can't reach another domain's tools through extraction.
        if (tools.FirstOrDefault(t => t.Name == toolName) is not { } tool)
            return (null, $"'{toolName}' isn't one of: {string.Join(", ", tools.Select(t => t.Name))}, none");
        return tool.Validate(args) is { } error ? (null, $"{toolName}: {error}") : (new ToolCall(toolName, args.Clone()), null);
    }
}
