using Microsoft.Extensions.Configuration;
using Ekko.PcAgent;

namespace Ekko.PcAgent.Tests;

public class PcAgentTests
{
    [Theory]
    [InlineData("pageup", new ushort[] { 0x21 })]
    [InlineData("ctrl+shift+m", new ushort[] { 0x11, 0x10, 0x4D })]
    [InlineData("Win + L", new ushort[] { 0x5B, 0x4C })]
    [InlineData("f13", new ushort[] { 0x7C })]
    [InlineData("volume_up", new ushort[] { 0xAF })]
    [InlineData("7", new ushort[] { 0x37 })]
    [InlineData("win+.", new ushort[] { 0x5B, 0xBE })]
    public void Key_names_map_to_virtual_key_codes(string combo, ushort[] expected) =>
        Assert.Equal(expected, Keyboard.Parse(combo));

    private static readonly Dictionary<string, string> Keys = new() { ["brain"] = "brain-key-123", ["deck"] = "deck-key-456" };

    [Theory]
    [InlineData("brain-key-123", "brain")]
    [InlineData("deck-key-456", "deck")]
    [InlineData("deck-key-45", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Each_key_identifies_its_client(string? supplied, string? client) =>
        Assert.Equal(client, ApiKeys.ClientFor(Keys, supplied));

    [Fact]
    public void An_empty_configured_key_never_matches() =>
        Assert.Null(ApiKeys.ClientFor(new Dictionary<string, string> { ["deck"] = "" }, "anything"));

    [Theory]
    [InlineData("pgup")]
    [InlineData("f25")]
    [InlineData("ctrl+")]
    public void Unknown_keys_are_rejected(string combo) =>
        Assert.Throws<ArgumentException>(() => Keyboard.Parse(combo));

    [Fact]
    public void Config_problems_are_caught_before_anything_runs()
    {
        Assert.Null(new PcAction { Keys = "pageup" }.Problem());
        Assert.Null(new PcAction { Lock = true }.Problem());
        Assert.NotNull(new PcAction().Problem());                                // nothing to do
        Assert.NotNull(new PcAction { Keys = "pageup", Lock = true }.Problem());  // two things
        Assert.NotNull(new PcAction { Keys = "pgup" }.Problem());                 // typo
        Assert.NotNull(new PcAction { Keys = "pageup", Repeat = 50 }.Problem());
    }

    [Fact]
    public async Task A_missing_program_fails_with_a_readable_message()
    {
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => new PcAction { Launch = @"C:\nope\missing.exe" }.RunAsync(default));
        Assert.Contains("missing.exe", ex.Message);
    }

    [Fact]
    public void The_shipped_config_is_valid()
    {
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        var actions = config.GetSection("Actions").Get<Dictionary<string, PcAction>>()!;
        Assert.All(actions, a => Assert.Null(a.Value.Problem()));
        // Every action id the Brain presses must exist here.
        foreach (var id in new[] { "discord_mute", "discord_deafen", "volume_up", "volume_down", "play_pause", "lock_pc", "steam" })
            Assert.Contains(id, actions.Keys);
    }
}
