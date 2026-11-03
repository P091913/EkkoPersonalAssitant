using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Ekko.Brain.Llm;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Pipeline;

public sealed record ChatReply([property: MinLength(1), MaxLength(400)] string Reply);

/// <summary>
/// The chat domain: a short spoken answer from a model with no tools, so it can talk but never act (DR-31). It goes through
/// <see cref="StructuredCall"/> like routing, so it gets the same retry, fallback and trace.
/// </summary>
public sealed class Chat(StructuredCall llm, ToolCatalog catalog)
{
    private static readonly JsonNode Schema = ToolJson.SchemaFor<ChatReply>();

    public async Task<string> ReplyAsync(string text, History history, TraceEvent t, CancellationToken ct) =>
        (await llm.AskAsync("chat", new LlmRequest(Prompt(history.Turns.Count > 0), history.Messages(text), Schema), StructuredCall.Parse<ChatReply>(), t, ct, history.Deep))
            ?.Reply.Trim() ?? "Sorry, I can't chat right now.";

    // Built from the catalog, so chat never offers a domain that has no tools yet.
    private string Prompt(bool withHistory) => $$"""
        You are Ekko, a home assistant, answering one message out loud.
        Reply in one to three short sentences of plain speech: no lists, markdown or emoji.
        You can talk: small talk, jokes, general knowledge, explaining things.
        But here you have no tools. You can't change, check or look up anything: not the PC, the TV, the budget sheet, the server, the web, or today's date and time.
        Never say or suggest that you did something, checked something, or will do it later.
        Ekko's commands cover {{string.Join(", ", Enum.GetValues<Domain>().Where(d => catalog.ForDomain(d).Count > 0).Select(Router.About))}}.
        If the message sounds like one of those commands, say you didn't catch it as a command and ask them to say it again plainly.
        If you don't know something, say so.
        Answer {"reply": "..."}.
        """ + (withHistory ? """

        Earlier turns are this conversation so far: answer the latest message in that light. What Ekko did in those turns
        was done by its commands; don't claim it as yours, and don't do anything new.
        """ : "");
}
