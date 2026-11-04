using System.Text.Json;
using System.Text.Json.Nodes;
using Ekko.Brain.Pipeline;

namespace Ekko.Brain.Tools;

/// <summary>The fixed domain list the router chooses from. Each tool belongs to exactly one.</summary>
public enum Domain { Desktop, Media, Tv, Budget, Food, Desk, Calendar, ServerOps, Chat }

/// <summary>What happens before a call runs. Read from <see cref="RiskClassAttribute"/> by <see cref="ToolGate"/>, never from a prompt.</summary>
public enum Risk { ReadOnly, Reversible, WriteConfirm, ExternalConfirm, Approve }

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RiskClassAttribute(Risk risk) : Attribute
{
    public Risk Risk { get; } = risk;
}

/// <param name="Say">A finished sentence for the user - tools do the math, not the model.</param>
/// <param name="Next">Set when the tool needs an answer before it can finish ("which one?").</param>
public sealed record ToolResult(bool Ok, string Say, Pending? Next = null)
{
    public static ToolResult Done(string say) => new(true, say);
    public static ToolResult Failed(string say) => new(false, say);
    public static ToolResult Ask(string question, Pending next) => new(true, question, next);
}

public interface ITool
{
    string Name { get; }
    Domain Domain { get; }
    string Description { get; }
    JsonNode Schema { get; }

    /// <summary>False for tools only code may call (e.g. resuming a pending choice): they're never offered to the model.</summary>
    bool ModelCanChoose { get; }

    /// <summary>Is this a well-formed call (schema, enums, ranges)? Null = yes. A model that gets this wrong is retried.</summary>
    string? Validate(JsonElement args);

    /// <summary>Is this call allowed (allowlists, policy)? Null = yes. A refusal goes to the user: retrying the model won't change it.</summary>
    string? Authorize(JsonElement args);

    /// <summary>The exact values a confirmation reads back.</summary>
    string Describe(JsonElement args);

    /// <summary>Call through <see cref="ToolGate"/>, not directly: the gate is what applies the risk class.</summary>
    Task<ToolResult> RunAsync(JsonElement args, CancellationToken ct);
}

/// <summary>For tools that take no arguments.</summary>
public sealed record NoArgs;

/// <summary>Base for tools whose arguments are a C# record.</summary>
public abstract class Tool<TArgs>(string name, Domain domain, string description) : ITool where TArgs : class
{
    // Generated once per args type. Shared, so DeepClone it before embedding it in a larger schema.
    private static readonly JsonNode CachedSchema = ToolJson.SchemaFor<TArgs>();

    public string Name => name;
    public Domain Domain => domain;
    public string Description => description;
    public JsonNode Schema => CachedSchema;
    public virtual bool ModelCanChoose => true;

    public string? Validate(JsonElement args) => ToolJson.TryParse<TArgs>(args, out _, out var error) ? null : error;

    public string? Authorize(JsonElement args) => Bind(args, out _);

    public string Describe(JsonElement args) => Bind(args, out var bound) is null ? Describe(bound!) : name;

    public async Task<ToolResult> RunAsync(JsonElement args, CancellationToken ct) =>
        Bind(args, out var bound) is { } error ? ToolResult.Failed(error) : await RunAsync(bound!, ct);

    /// <summary>Allowlists and rules a schema can't express. Null = allowed.</summary>
    protected virtual string? Check(TArgs args) => null;

    protected virtual string Describe(TArgs args) => $"{name} {JsonSerializer.Serialize(args, ToolJson.Options)}";

    protected abstract Task<ToolResult> RunAsync(TArgs args, CancellationToken ct);

    private string? Bind(JsonElement json, out TArgs? args) =>
        ToolJson.TryParse(json, out args, out var error) ? Check(args) : error;
}
