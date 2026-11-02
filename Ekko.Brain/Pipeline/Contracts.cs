namespace Ekko.Brain.Pipeline;

/// <summary>What every channel hands the Brain: text, which channel it came from, and the conversation it belongs to.</summary>
public sealed record MessageRequest(string Text, string Channel, string ConversationId)
{
    public const int MaxTextLength = 500;
    private const int MaxIdLength = 100;

    /// <summary>Null when the request is usable; otherwise the reason, for a 400.</summary>
    public string? Error() =>
        string.IsNullOrWhiteSpace(Text) ? "text is required"
        : Text.Length > MaxTextLength ? $"text is limited to {MaxTextLength} characters"
        : string.IsNullOrWhiteSpace(Channel) || Channel.Length > MaxIdLength ? "channel is required (max 100 characters)"
        : string.IsNullOrWhiteSpace(ConversationId) || ConversationId.Length > MaxIdLength ? "conversationId is required (max 100 characters)"
        : null;
}

/// <param name="Text">One short sentence for the channel to show or speak.</param>
/// <param name="RequestId">Matches the request_id on this turn's trace lines.</param>
public sealed record MessageReply(string Text, string RequestId);
