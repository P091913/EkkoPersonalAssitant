using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Desk;

public sealed record AddDeskNoteArgs(
    [property: MinLength(1), Description("Only the words to write on the note: 'put a note on my desk: call mom' is 'call mom'.")] string Text);

/// <summary>
/// A sticky note on the projected desk (DR-39). Reversible: it lands at once, and dragging it to the desk's trash takes it off.
/// </summary>
[RiskClass(Risk.Reversible)]
public sealed class AddDeskNote(DeskService desk)
    : Tool<AddDeskNoteArgs>("add_desk_note", Domain.Desk,
        "Put a sticky note on the user's projected desk: 'put a note on my desk: call mom', 'stick a note on my desk saying buy milk', " +
        "'add a desk note to email Sam'.")
{
    /// <summary>The desk's own limit. Checked here too, because a long note is the user's words, not a model mistake to retry.</summary>
    public const int MaxLength = 200;

    protected override string? Check(AddDeskNoteArgs args) => args.Text.Trim() switch
    {
        "" => "the note has no words",
        { Length: > MaxLength } => $"a desk note is limited to {MaxLength} characters",
        _ => null,
    };

    protected override Task<ToolResult> RunAsync(AddDeskNoteArgs args, CancellationToken ct) => desk.AddNoteAsync(args.Text.Trim(), ct);
}
