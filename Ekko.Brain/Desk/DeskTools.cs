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
        "Put a sticky note with the user's words on their projected desk: 'put a note on my desk: call mom', " +
        "'stick a note on my desk saying buy milk', 'add a desk note to email Sam'.")
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

public sealed record ShowDeskFileArgs(
    [property: MaxLength(60), Description("Whenever the user names the file, the words that name it: from its name ('invoice', 'lease') or its kind ('photo', 'pdf'). Leave it out only when they don't name it: 'put it on my desk', 'pull that up on the desk'.")]
    string? Name = null);

/// <summary>
/// Opens a photo or PDF on the desk: one the user sent Ekko (DR-40) or saved in Downloads (DR-41), both found through
/// Ekko.Files. Reversible: closing the card puts it away, and the file stays. Which file the words mean is the store's
/// call, not the model's.
/// </summary>
[RiskClass(Risk.Reversible)]
public sealed class ShowDeskFile(DeskService desk)
    : Tool<ShowDeskFileArgs>("show_file_on_desk", Domain.Desk,
        "Open a photo or PDF the user sent Ekko or downloaded on their projected desk: 'put it on my desk', 'show the invoice on my desk', " +
        "'pull up the photo I sent on my desk', 'pull that up on the desk'.")
{
    protected override Task<ToolResult> RunAsync(ShowDeskFileArgs args, CancellationToken ct) => desk.ShowFileAsync(args.Name?.Trim() ?? "", ct);
}
