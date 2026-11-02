using Windows.Media.Control;

namespace Ekko.PcAgent;

public sealed record NowPlayingState(
    bool Active, bool Playing, string Status, string? App, string? Title, string? Artist, string? Album,
    double? PositionSeconds, double? DurationSeconds);

/// <summary>What Windows' media session reports - Spotify, a browser tab, any app that shows in the volume flyout.</summary>
public static class NowPlaying
{
    private static GlobalSystemMediaTransportControlsSessionManager? _manager;

    public static async Task<NowPlayingState> ReadAsync(CancellationToken ct)
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct);
        // Prefer a session that's actually playing: Windows' "current" session can be a paused tab.
        var session = _manager.GetSessions().FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                      ?? _manager.GetCurrentSession();
        if (session is null)
            return new(false, false, "none", null, null, null, null, null, null);

        var media = await session.TryGetMediaPropertiesAsync().AsTask(ct);
        var status = session.GetPlaybackInfo().PlaybackStatus;
        var timeline = session.GetTimelineProperties();
        return new(
            true,
            status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            status.ToString().ToLowerInvariant(),
            session.SourceAppUserModelId,
            media.Title, media.Artist, media.AlbumTitle,
            timeline.EndTime > TimeSpan.Zero ? timeline.Position.TotalSeconds : null,
            timeline.EndTime > TimeSpan.Zero ? timeline.EndTime.TotalSeconds : null);
    }
}
