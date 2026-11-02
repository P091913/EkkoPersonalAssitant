using System.Reflection;
using System.Text.Json;
using Ekko.Brain.Pipeline;

namespace Ekko.Brain.Tools;

/// <summary>Ekko read back a call and is waiting for "yes".</summary>
public sealed record PendingConfirm(string Tool, JsonElement Args) : Pending;

/// <summary>
/// The only path from a chosen tool call to execution - fast path and model alike. The model decides what to call;
/// the tool's [RiskClass] decides what must happen first.
/// </summary>
public static class ToolGate
{
    public static Risk? RiskOf(ITool tool) => tool.GetType().GetCustomAttribute<RiskClassAttribute>()?.Risk;

    /// <param name="confirmed">True only when the user just said yes to this exact <see cref="PendingConfirm"/>.</param>
    public static async Task<ToolResult> ExecuteAsync(ITool tool, JsonElement args, bool confirmed, CancellationToken ct)
    {
        // Fail closed: a tool nobody classified doesn't run.
        if (RiskOf(tool) is not { } risk)
            return ToolResult.Failed($"{tool.Name} has no risk class, so it can't run.");
        if ((tool.Validate(args) ?? tool.Authorize(args)) is { } error)
            return ToolResult.Failed($"I can't do that: {error}.");

        return risk switch
        {
            Risk.ReadOnly or Risk.Reversible => await tool.RunAsync(args, ct),
            Risk.WriteConfirm or Risk.ExternalConfirm when !confirmed => Confirm(tool.Name, tool.Describe(args), args),
            Risk.WriteConfirm or Risk.ExternalConfirm => await tool.RunAsync(args, ct),
            // shortcut: Approve (push to phone, resume the saved run) arrives with server ops; until then it's refused.
            _ => ToolResult.Failed($"{tool.Name} needs approval from your phone, which isn't built yet."),
        };
    }

    /// <summary>
    /// The one way to ask "go ahead?". The gate asks it for confirm-class tools; a lookup asks it once it knows exactly
    /// what a write will touch. Either way the yes runs <paramref name="tool"/> back through the gate.
    /// </summary>
    public static ToolResult Confirm(string tool, string describe, JsonElement args) =>
        ToolResult.Ask($"{describe}. Go ahead?", new PendingConfirm(tool, args.Clone()));
}

/// <summary>Every tool, findable by name (fast path, resumed confirmations) or by domain (handlers).</summary>
public sealed class ToolCatalog(IEnumerable<ITool> tools)
{
    private readonly Dictionary<string, ITool> _byName = tools.ToDictionary(t => t.Name);

    public ITool? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>The tools a model may pick for a domain. Code-only tools are reachable through <see cref="Find"/> alone.</summary>
    public IReadOnlyList<ITool> ForDomain(Domain domain) => _byName.Values.Where(t => t.Domain == domain && t.ModelCanChoose).ToList();

    public IEnumerable<ITool> All => _byName.Values;
}
