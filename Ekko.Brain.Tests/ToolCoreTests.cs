using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Tests;

public enum Speed { Slow, VeryFast }

public sealed record SampleArgs(
    [property: Description("How fast.")] Speed Speed,
    [property: Range(1, 5)] int Steps = 1,
    string? Note = null);

public abstract class CountingTool(string name) : Tool<SampleArgs>(name, Domain.Desktop, "test tool")
{
    public int Runs { get; private set; }

    protected override Task<ToolResult> RunAsync(SampleArgs args, CancellationToken ct)
    {
        Runs++;
        return Task.FromResult(ToolResult.Done($"ran {args.Speed} x{args.Steps}"));
    }
}

[RiskClass(Risk.Reversible)] public sealed class ReversibleTool() : CountingTool("reversible");
[RiskClass(Risk.WriteConfirm)] public sealed class ConfirmTool() : CountingTool("confirm");
[RiskClass(Risk.Approve)] public sealed class ApproveTool() : CountingTool("approve");
public sealed class UnclassifiedTool() : CountingTool("unclassified");

public class ToolCoreTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly JsonElement Valid = Json("""{"speed":"very_fast","steps":2}""");

    [Fact]
    public void Schema_comes_from_the_args_record()
    {
        var schema = new ReversibleTool().Schema.ToJsonString();
        Assert.Contains("\"enum\":[\"slow\",\"very_fast\"]", schema);
        Assert.Contains("\"description\":\"How fast.\"", schema);
        Assert.Contains("\"minimum\":1", schema);
        Assert.Contains("\"maximum\":5", schema);
        Assert.Contains("\"required\":[\"speed\"]", schema);
        Assert.Contains("\"additionalProperties\":false", schema);
    }

    [Theory]
    [InlineData("""{"speed":"warp"}""")]               // not in the enum
    [InlineData("""{"speed":"slow","steps":9}""")]     // out of range
    [InlineData("""{"steps":2}""")]                    // missing required
    [InlineData("""{"speed":"slow","volume":3}""")]    // invented argument
    [InlineData("""{"speed":1}""")]                    // enum as a number
    [InlineData("null")]
    public void Bad_args_are_rejected(string json) => Assert.NotNull(new ReversibleTool().Validate(Json(json)));

    [Fact]
    public async Task Reversible_runs()
    {
        var tool = new ReversibleTool();
        var result = await ToolGate.ExecuteAsync(tool, Valid, confirmed: false, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal("ran VeryFast x2", result.Say);
    }

    [Fact]
    public async Task Invalid_args_never_reach_the_tool()
    {
        var tool = new ReversibleTool();
        var result = await ToolGate.ExecuteAsync(tool, Json("""{"speed":"warp"}"""), confirmed: false, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(0, tool.Runs);
    }

    [Fact]
    public async Task A_tool_without_a_risk_class_is_refused()
    {
        var tool = new UnclassifiedTool();
        var result = await ToolGate.ExecuteAsync(tool, Valid, confirmed: true, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(0, tool.Runs);
    }

    [Fact]
    public async Task Approve_is_held_until_approved()
    {
        var tool = new ApproveTool();
        var held = await ToolGate.ExecuteAsync(tool, Valid, confirmed: false, CancellationToken.None);
        Assert.IsType<PendingApproval>(held.Next);
        Assert.Equal(0, tool.Runs);

        await ToolGate.ExecuteAsync(tool, Valid, confirmed: true, CancellationToken.None);
        Assert.Equal(1, tool.Runs);
    }

    [Fact]
    public async Task Confirm_class_reads_back_and_waits_then_runs_once_confirmed()
    {
        var tool = new ConfirmTool();
        var asked = await ToolGate.ExecuteAsync(tool, Valid, confirmed: false, CancellationToken.None);
        Assert.Equal(0, tool.Runs);
        Assert.Contains("very_fast", asked.Say);
        Assert.IsType<PendingConfirm>(asked.Next);

        await ToolGate.ExecuteAsync(tool, Valid, confirmed: true, CancellationToken.None);
        Assert.Equal(1, tool.Runs);
    }

    [Theory]
    [InlineData("Yes please!", 1, "ran VeryFast x2")]
    [InlineData("nah, never mind", 0, "Sorry, I didn't catch that.")] // not an exact "no": treated as a new request
    [InlineData("never mind", 0, "Okay, cancelled.")]
    [InlineData("mute discord", 0, "Sorry, I didn't catch that.")]
    public async Task Pipeline_resumes_a_pending_confirmation(string answer, int expectedRuns, string expectedReply)
    {
        using var temp = new TempTrace();
        var tool = new ConfirmTool();
        var store = new ConversationStore(TimeProvider.System, TimeSpan.FromMinutes(3));
        store.Set("c1", new PendingConfirm("confirm", Valid));
        var pipeline = TestPipeline.Create(store, new ToolCatalog([tool]), temp.TraceLog);

        var reply = await pipeline.HandleAsync(new MessageRequest(answer, "test", "c1"), CancellationToken.None);

        Assert.Equal(expectedRuns, tool.Runs);
        Assert.Equal(expectedReply, reply.Text);
        Assert.Null(store.Take("c1")); // answered or abandoned, either way it's gone
    }
}
