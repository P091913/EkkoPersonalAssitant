using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Media;

public enum MediaKind { Movie, Series }

public enum PlaybackAction { Pause, Resume, Stop }

public sealed record PlayMediaArgs(
    [property: MinLength(1), Description("Only the title, as the user said it - without words like play, watch, movie or season.")] string Title,
    [property: Description("Only if the user said the word movie/film or show/series. Never infer it from the title or a season.")] MediaKind? Kind = null,
    [property: Range(0, 100), Description("Only if the user named a season.")] int? Season = null,
    [property: Range(1, 999), Description("Only if the user named an episode.")] int? Episode = null);

public sealed record MediaItemArgs(string ItemId);

public sealed record MediaControlArgs(PlaybackAction Action);

[RiskClass(Risk.Reversible)]
public sealed class PlayMedia(TvRemote tv)
    : Tool<PlayMediaArgs>("play_media", Domain.Media, "Play a show or movie from the library on the TV. Wakes the TV if needed.")
{
    protected override async Task<ToolResult> RunAsync(PlayMediaArgs args, CancellationToken ct) =>
        MediaResult.From(await tv.PostAsync("tv/play",
            new { title = args.Title, kind = args.Kind?.ToString().ToLowerInvariant(), season = args.Season, episode = args.Episode }, ct));
}

/// <summary>Plays the candidate the user picked from Ekko's "which one?". Code-only: the model never sees it, so it can't invent ids.</summary>
[RiskClass(Risk.Reversible)]
public sealed class PlayMediaItem(TvRemote tv)
    : Tool<MediaItemArgs>(ToolName, Domain.Media, "Play one candidate the user picked.")
{
    public const string ToolName = "play_media_item";

    public override bool ModelCanChoose => false;

    protected override async Task<ToolResult> RunAsync(MediaItemArgs args, CancellationToken ct) =>
        MediaResult.From(await tv.PostAsync("tv/play", new { itemId = args.ItemId }, ct));
}

[RiskClass(Risk.Reversible)]
public sealed class MediaControl(TvRemote tv)
    : Tool<MediaControlArgs>("media_control", Domain.Media, "Pause, resume or stop whatever is already playing on the TV. If the user names a title, use play_media instead.")
{
    protected override async Task<ToolResult> RunAsync(MediaControlArgs args, CancellationToken ct) =>
        MediaResult.From(await tv.PostAsync($"tv/{args.Action.ToString().ToLowerInvariant()}", null, ct));
}

internal static class MediaResult
{
    /// <summary>tvremote already words every outcome; candidates turn into a pending choice.</summary>
    public static ToolResult From(TvAnswer answer) => answer.Status switch
    {
        "playing" or "ok" => ToolResult.Done(answer.Message),
        "ambiguous" or "not_found" when answer.Candidates.Count > 0 =>
            ToolResult.Ask(answer.Message, new PendingChoice(PlayMediaItem.ToolName,answer.Candidates.Select(Option).ToList())),
        _ => ToolResult.Failed(answer.Message),
    };

    private static ChoiceOption Option(MediaCandidate c)
    {
        var keywords = Utterance.Simplify(c.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (c.ProductionYear is int year)
            keywords.Add(year.ToString());
        keywords.AddRange(c.Type switch
        {
            "Movie" => ["movie", "film"],
            "Series" => ["show", "series", "tv"],
            "Episode" => ["episode"],
            _ => [],
        });
        return new ChoiceOption(c.Label, JsonSerializer.SerializeToElement(new MediaItemArgs(c.Id), ToolJson.Options), keywords);
    }
}
