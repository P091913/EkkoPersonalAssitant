using System.Diagnostics;
using System.Text.Json;
using Ekko.Brain.ServerOps;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Pipeline;

/// <summary>pending state -> fast path -> router -> domain handler -> reply. Each request stops at the first step that can handle it.</summary>
public sealed class MessagePipeline(ConversationStore conversations, ToolCatalog catalog, Router router, DomainHandler handler, ServerOpsAgent agent,
    Chat chat, TraceLog trace)
{
    public async Task<MessageReply> HandleAsync(MessageRequest request, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var t = new TraceEvent(Guid.NewGuid().ToString("N")[..12], request.ConversationId, request.Channel);
        trace.Write(t with { Step = "request", Input = request.Text });
        try
        {
            var pending = conversations.Take(request.ConversationId);
            trace.Write(t with { Step = "pending", Hit = pending is not null, Result = pending?.GetType().Name });
            if (pending is not null && await ResumeAsync(pending, request, t, ct) is { } resumed)
                return Reply(t, resumed, started);

            // A server-ops run waiting for approval lives on disk, not in the short-lived conversation state.
            var approve = Utterance.IsApprove(request.Text);
            if ((approve || Utterance.IsDeny(request.Text)) && agent.WaitingFor(request.ConversationId) is { } run)
            {
                trace.Write(t with { Step = "pending", Hit = true, Result = $"run {run.Id}" });
                return Reply(t, await agent.ResumeAsync(run.Id, approve, t, ct), started);
            }

            if (FastPath.Match(request.Text) is { } fast && catalog.Find(fast.Tool) is { } fastTool)
            {
                trace.Write(t with { Step = "fast_path", Hit = true, Tool = fast.Tool, Args = fast.Args });
                return Reply(t, await RunToolAsync(fastTool, fast.Args, confirmed: false, request, t, ct), started);
            }
            trace.Write(t with { Step = "fast_path", Hit = false });

            var route = await router.RouteAsync(request.Text, t, ct);
            if (route is null)
                return Reply(t, "Sorry, I didn't catch that.", started);
            trace.Write(t with { Step = "route", Domain = Router.Name(route.Domain), Result = route.Confidence.ToString().ToLowerInvariant() });

            // Asking only helps if the answer could lead somewhere: an unbuilt domain gets its "not yet" straight away.
            if (route.Confidence == Confidence.Low && catalog.ForDomain(route.Domain).Count > 0)
            {
                conversations.Set(request.ConversationId, new PendingRoute(route.Domain, request.Text));
                return Reply(t, $"Is that about {Router.About(route.Domain)}?", started);
            }
            return Reply(t, await HandleDomainAsync(route.Domain, request.Text, request, t, ct), started);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A voice channel needs a sentence, not a 500 - and the failure belongs in the trace.
            trace.Write(t with { Step = "error", Error = ex.ToString() });
            return Reply(t, "Something went wrong on my end.", started);
        }
    }

    /// <summary>An approval from outside a conversation: the deck, or a phone later. Traced like a message.</summary>
    public async Task<MessageReply> ResolveRunAsync(string runId, bool approved, string channel, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var t = new TraceEvent(Guid.NewGuid().ToString("N")[..12], $"run:{runId}", channel);
        trace.Write(t with { Step = "request", Input = approved ? "approve" : "deny" });
        return Reply(t, await agent.ResumeAsync(runId, approved, t, ct), started);
    }

    /// <summary>Null when the message isn't an answer to what was pending; it's then handled as a new request.</summary>
    private async Task<string?> ResumeAsync(Pending pending, MessageRequest request, TraceEvent t, CancellationToken ct)
    {
        switch (pending)
        {
            case PendingConfirm confirm when Utterance.IsYes(request.Text) && catalog.Find(confirm.Tool) is { } tool:
                return await RunToolAsync(tool, confirm.Args, confirmed: true, request, t, ct);
            case PendingConfirm when Utterance.IsNo(request.Text):
                return "Okay, cancelled.";
            case PendingRoute route when Utterance.IsYes(request.Text):
                return await HandleDomainAsync(route.Domain, route.Text, request, t, ct);
            case PendingRoute when Utterance.IsNo(request.Text):
                return "Okay. Can you say it another way?";
            case PendingChoice choice when choice.Pick(request.Text) is { } option && catalog.Find(choice.Tool) is { } chosen:
                return await RunToolAsync(chosen, option.Args, confirmed: false, request, t, ct);
            case PendingChoice when Utterance.IsNo(request.Text):
                return "Okay, never mind.";
            default:
                return null;
        }
    }

    /// <param name="text">The command to act on - after a "yes" to "is that about the PC?", the original one.</param>
    private async Task<string> HandleDomainAsync(Domain domain, string text, MessageRequest request, TraceEvent t, CancellationToken ct)
    {
        // The one domain that investigates in several steps instead of extracting one call (DR-4).
        if (domain == Domain.ServerOps)
            return await agent.StartAsync(text, request.ConversationId, t, ct);
        // The one domain with no tools on purpose: it answers, it never acts (DR-31).
        if (domain == Domain.Chat)
            return await chat.ReplyAsync(text, t, ct);
        if (catalog.ForDomain(domain).Count == 0)
            return $"I can't help with {Router.About(domain)} yet.";

        var call = await handler.ExtractAsync(domain, text, t, ct);
        trace.Write(t with { Step = "extract", Domain = Router.Name(domain), Tool = call?.Tool, Args = call?.Args, Ok = call is not null });
        if (call is null)
            return "Sorry, I couldn't work out what to do.";
        if (call.Tool == ToolCall.None || catalog.Find(call.Tool) is not { } tool)
            return "I can't do that yet.";
        return await RunToolAsync(tool, call.Args, confirmed: false, request, t, ct);
    }

    private async Task<string> RunToolAsync(ITool tool, JsonElement args, bool confirmed, MessageRequest request, TraceEvent t,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await ToolGate.ExecuteAsync(tool, args, confirmed, ct);
        trace.Write(t with { Step = "tool", Tool = tool.Name, Args = args, Ok = result.Ok, Result = result.Say, LatencyMs = Ms(started) });
        if (result.Next is not null)
            conversations.Set(request.ConversationId, result.Next);
        return result.Say;
    }

    private MessageReply Reply(TraceEvent t, string text, long started)
    {
        trace.Write(t with { Step = "reply", Result = text, LatencyMs = Ms(started) });
        return new MessageReply(text, t.RequestId);
    }

    private static long Ms(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
