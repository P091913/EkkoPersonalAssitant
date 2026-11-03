using System.Text.Json;
using Ekko.Brain.Pipeline;

namespace Ekko.Brain.Tests;

public class TraceViewTests
{
    private static readonly TraceEvent Turn = new("r1", "c1", "deck");

    [Fact]
    public void Views_show_what_happened_but_not_prompts_or_model_output()
    {
        using var temp = new TempTrace();
        temp.TraceLog.Write(Turn with { Step = "request", Input = "lock my pc" });
        temp.TraceLog.Write(Turn with { Step = "llm:route", Model = "qwen", Input = "PROMPT", Output = "RAW", Result = "end_turn", TokensIn = 10 });
        temp.TraceLog.Write(Turn with { Step = "error", Error = "System.Exception: boom\n   at Somewhere()" });
        temp.TraceLog.Write(Turn with { Step = "reply", Result = new string('a', 300) });

        var views = temp.TraceLog.Since(0);
        Assert.Equal([1L, 2, 3, 4], views.Select(v => v.Seq));
        Assert.Equal("lock my pc", views[0].Text);
        Assert.Null(views[1].Text);
        Assert.Equal(10, views[1].TokensIn);
        var json = JsonSerializer.Serialize(views);
        Assert.DoesNotContain("PROMPT", json);
        Assert.DoesNotContain("RAW", json);
        Assert.Equal("System.Exception: boom", views[2].Error);
        Assert.Equal(160, views[3].Text!.Length);
        Assert.Equal([3L, 4], temp.TraceLog.Since(2).Select(v => v.Seq));
    }

    [Fact]
    public void Only_the_newest_steps_are_kept()
    {
        using var temp = new TempTrace();
        for (var i = 0; i < TraceLog.Keep + 5; i++)
            temp.TraceLog.Write(Turn with { Step = "tool" });

        var views = temp.TraceLog.Since(0);
        Assert.Equal(TraceLog.Keep, views.Length);
        Assert.Equal(6, views[0].Seq);
    }

    [Fact]
    public void A_restart_picks_up_the_newest_steps_from_the_file_with_a_new_boot()
    {
        using var temp = new TempTrace();
        temp.TraceLog.Write(Turn with { Step = "request", Input = "hi" });
        File.AppendAllText(temp.Path, "{\"ts\":\"2026-11-03T00:00:00+00:00\",\"request_id\":\"r2\",\"conv\n"); // cut short by a crash
        temp.TraceLog.Write(Turn with { Step = "reply", Result = "hello", LatencyMs = 5 });

        var restarted = new TraceLog(temp.Path, TimeProvider.System);
        var views = restarted.Since(0);
        Assert.Equal(["request", "reply"], views.Select(v => v.Step));
        Assert.Equal("deck", views[1].Channel);
        Assert.Equal(5, views[1].LatencyMs);
        Assert.NotEqual(default, views[0].Ts);
        Assert.NotEqual(temp.TraceLog.Boot, restarted.Boot);
    }
}
