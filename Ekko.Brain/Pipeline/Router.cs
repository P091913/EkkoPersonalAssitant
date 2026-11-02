using System.Text.Json;
using System.Text.Json.Nodes;
using Ekko.Brain.Llm;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Pipeline;

public enum Confidence { High, Low }

public sealed record RouteDecision(Domain Domain, Confidence Confidence);

/// <summary>Ekko asked "is that about the PC?" because the router wasn't sure.</summary>
public sealed record PendingRoute(Domain Domain, string Text) : Pending;

/// <summary>One model call that picks one domain from the fixed list. The router never sees tools - that's the handler's job.</summary>
public sealed class Router(StructuredCall llm)
{
    private static readonly JsonNode Schema = ToolJson.SchemaFor<RouteDecision>();

    private const string Prompt = """
        You route one voice command for Ekko, a home assistant, to exactly one domain.
        desktop: the PC. Opening apps (Steam, Discord, browser), Discord mute or deafen, PC volume, locking the PC, play/pause of music or video on the PC.
        media: shows and movies on the TV. Playing a title, and pausing, resuming or stopping what the TV is playing. A bare "pause", "resume" or "stop" means the TV.
        tv: the TV itself. Turning it on or off, opening Jellyfin on it.
        budget: money, spending, budget categories, logging an expense.
        food: logging what was eaten, or asking about it.
        calendar: events, schedule, meetings, free time.
        server_ops: the home server. Containers, disk space, logs, restarts.
        chat: anything else, such as questions and small talk.
        Naming a show or movie the user wants to see is media, even without the word "play".
        confidence is "low" only when two domains fit about equally well or the request is unclear; otherwise "high".
        """;

    public Task<RouteDecision?> RouteAsync(string text, TraceEvent t, CancellationToken ct) =>
        llm.AskAsync("route", new LlmRequest(Prompt, [new LlmMessage("user", text)], Schema), StructuredCall.Parse<RouteDecision>(), t, ct);

    public static string Name(Domain domain) => JsonNamingPolicy.SnakeCaseLower.ConvertName(domain.ToString());

    /// <summary>How a reply refers to a domain.</summary>
    public static string About(Domain domain) => domain switch
    {
        Domain.Desktop => "the PC",
        Domain.Media => "shows and movies on the TV",
        Domain.Tv => "the TV itself",
        Domain.Budget => "your budget",
        Domain.Food => "your food log",
        Domain.Calendar => "your calendar",
        Domain.ServerOps => "the server",
        _ => "chatting",
    };
}
