using System.Net;
using System.Text.Json;
using Ekko.Brain.Desktop;
using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Tests;

public class DesktopTests
{
    private readonly FakeHttp _pc = new();

    // Every real tool, so the catalog-wide checks below cover all domains.
    private ToolCatalog Catalog() => new([.. TestPipeline.DesktopTools(_pc), .. TestPipeline.MediaTools(new FakeHttp())]);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private Task<ToolResult> Run(string tool, string args) =>
        ToolGate.ExecuteAsync(Catalog().Find(tool)!, Json(args), confirmed: false, CancellationToken.None);

    [Fact]
    public void Every_tool_has_a_risk_class() =>
        Assert.All(Catalog().All, t => Assert.NotNull(ToolGate.RiskOf(t)));

    [Fact]
    public void Every_fast_path_phrase_names_a_real_tool_with_valid_args()
    {
        var catalog = Catalog();
        Assert.All(FastPath.Phrases, phrase =>
        {
            var match = FastPath.Match(phrase)!;
            var tool = catalog.Find(match.Tool);
            Assert.True(tool is not null, $"'{phrase}' -> unknown tool {match.Tool}");
            Assert.Null(tool.Validate(match.Args));
        });
    }

    [Theory]
    [InlineData("Hey Ekko, can you mute my Discord, please?", "discord_toggle_mute")]
    [InlineData("Lock the PC.", "lock_pc")]
    [InlineData("VOLUME UP!", "adjust_volume")]
    public void Fast_path_ignores_filler_and_punctuation(string text, string tool) =>
        Assert.Equal(tool, FastPath.Match(text)?.Tool);

    [Theory]
    [InlineData("mute")]                 // ambiguous: Discord, system, TV?
    [InlineData("mute discord and open steam")]
    [InlineData("pause it in a minute")]
    public void Fast_path_only_takes_exact_phrases(string text) => Assert.Null(FastPath.Match(text));

    [Theory]
    [InlineData("Pause.", "pause")]      // bare pause/resume/stop mean the TV
    [InlineData("pause the TV", "pause")]
    [InlineData("resume", "resume")]
    [InlineData("stop playing", "stop")]
    public void Bare_playback_words_go_to_the_tv(string text, string action)
    {
        var match = FastPath.Match(text)!;
        Assert.Equal("media_control", match.Tool);
        Assert.Equal(action, match.Args.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Volume_steps_press_the_button_that_many_times()
    {
        var result = await Run("adjust_volume", """{"direction":"up","steps":3}""");
        Assert.Equal("Volume up, 3 notches.", result.Say);
        Assert.Equal(3, _pc.Requests.Count(r => r is { Method.Method: "POST", Path: "/action/volume_up" }));
    }

    [Fact]
    public async Task Apps_off_the_allowlist_are_refused_without_calling_the_pc()
    {
        var result = await Run("open_app", """{"app":"discord"}""");
        Assert.False(result.Ok);
        Assert.Contains("allowlist", result.Say);
        Assert.Empty(_pc.Requests);
    }

    [Fact]
    public async Task Allowed_app_presses_its_configured_action()
    {
        Assert.Equal("Opening Steam.", (await Run("open_app", """{"app":"steam"}""")).Say);
        Assert.Equal("/action/steam", _pc.Requests.Single().Path);
    }

    [Fact]
    public async Task Play_pause_is_an_agent_action()
    {
        await Run("media_play_pause", "{}");
        Assert.Equal("/action/play_pause", _pc.Requests.Single().Path);
    }

    [Fact]
    public async Task Pc_errors_become_sentences()
    {
        _pc.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Equal("The PC is busy with another action.", (await Run("lock_pc", "{}")).Say);

        _pc.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = new StringContent("""{"ok":false,"error":"no action named 'x'"}""") };
        Assert.Equal("The PC couldn't do that: no action named 'x'.", (await Run("lock_pc", "{}")).Say);

        _pc.Requests.Clear();
        _pc.Respond = _ => throw new HttpRequestException("connection refused");
        var down = await Run("adjust_volume", """{"direction":"down","steps":2}""");
        Assert.False(down.Ok);
        Assert.Equal("The PC isn't answering.", down.Say);
        Assert.Single(_pc.Requests); // stopped at the first failure
    }

    [Fact]
    public async Task Pipeline_runs_a_fast_path_hit_through_the_gate_and_traces_it()
    {
        using var temp = new TempTrace();
        var pipeline = TestPipeline.Create(new ConversationStore(TimeProvider.System, TimeSpan.FromMinutes(3)), Catalog(), temp.TraceLog);

        var reply = await pipeline.HandleAsync(new MessageRequest("Volume up, please!", "test", "c1"), CancellationToken.None);

        Assert.Equal("Volume up.", reply.Text);
        Assert.Equal("/action/volume_up", _pc.Requests.Single().Path);
        var lines = temp.Lines();
        Assert.Equal(["request", "pending", "fast_path", "tool", "reply"], lines.Select(l => l.GetProperty("step").GetString()));
        Assert.True(lines[2].GetProperty("hit").GetBoolean());
        Assert.Equal("up", lines[3].GetProperty("args").GetProperty("direction").GetString());
        Assert.True(lines[3].GetProperty("ok").GetBoolean());
    }
}
