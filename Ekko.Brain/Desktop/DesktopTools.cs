using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Desktop;

public enum App { Steam, Discord, Browser }

public enum Direction { Up, Down }

public sealed record AppArgs([property: Description("Which app to open.")] App App);

public sealed record VolumeArgs(
    Direction Direction,
    [property: Range(1, 5), Description("How many notches, each about 6%. 1 unless the user asks for a lot.")] int Steps = 1);

[RiskClass(Risk.Reversible)]
public sealed class OpenApp(PcService pc, PcServiceOptions options)
    : Tool<AppArgs>("open_app", Domain.Desktop, "Open an app on the PC.")
{
    private static string Key(App app) => app.ToString().ToLowerInvariant();

    protected override string? Check(AppArgs args) =>
        options.Apps.ContainsKey(Key(args.App)) ? null : $"{args.App} isn't on the app allowlist yet";

    protected override Task<ToolResult> RunAsync(AppArgs args, CancellationToken ct) =>
        pc.PressAsync(options.Apps[Key(args.App)], $"Opening {args.App}.", ct);
}

// The PC service presses a hotkey and can't read Discord's state, so these toggle - there is no honest "set".
[RiskClass(Risk.Reversible)]
public sealed class DiscordToggleMute(PcService pc)
    : Tool<NoArgs>("discord_toggle_mute", Domain.Desktop, "Toggle my Discord microphone mute. Mute and unmute are the same call.")
{
    protected override Task<ToolResult> RunAsync(NoArgs args, CancellationToken ct) =>
        pc.PressAsync("discord_mute", "Toggled Discord mute.", ct);
}

[RiskClass(Risk.Reversible)]
public sealed class DiscordToggleDeafen(PcService pc)
    : Tool<NoArgs>("discord_toggle_deafen", Domain.Desktop, "Toggle Discord deafen. Deafen and undeafen are the same call.")
{
    protected override Task<ToolResult> RunAsync(NoArgs args, CancellationToken ct) =>
        pc.PressAsync("discord_deafen", "Toggled Discord deafen.", ct);
}

[RiskClass(Risk.Reversible)]
public sealed class AdjustVolume(PcService pc)
    : Tool<VolumeArgs>("adjust_volume", Domain.Desktop, "Turn the PC's volume up or down by notches. It can't set an exact level.")
{
    protected override async Task<ToolResult> RunAsync(VolumeArgs args, CancellationToken ct)
    {
        var action = args.Direction == Direction.Up ? "volume_up" : "volume_down";
        for (var i = 0; i < args.Steps; i++)
        {
            var press = await pc.PressAsync(action, "", ct);
            if (!press.Ok)
                return press;
        }
        var direction = args.Direction.ToString().ToLowerInvariant();
        return ToolResult.Done(args.Steps == 1 ? $"Volume {direction}." : $"Volume {direction}, {args.Steps} notches.");
    }
}

[RiskClass(Risk.Reversible)]
public sealed class MediaPlayPause(PcService pc)
    : Tool<NoArgs>("media_play_pause", Domain.Desktop, "Play or pause music or video on the PC - not the TV.")
{
    protected override Task<ToolResult> RunAsync(NoArgs args, CancellationToken ct) =>
        pc.PressAsync("play_pause", "Toggled play/pause on the PC.", ct);
}

[RiskClass(Risk.Reversible)]
public sealed class LockPc(PcService pc) : Tool<NoArgs>("lock_pc", Domain.Desktop, "Lock the PC.")
{
    protected override Task<ToolResult> RunAsync(NoArgs args, CancellationToken ct) =>
        pc.PressAsync("lock_pc", "Locking the PC.", ct);
}
