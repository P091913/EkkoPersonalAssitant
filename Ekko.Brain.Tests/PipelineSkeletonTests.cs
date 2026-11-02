using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Tests;

public class PipelineSkeletonTests
{
    private sealed record TestPending : Pending;

    [Fact]
    public void Pending_is_taken_once_and_expires()
    {
        var clock = new ManualClock();
        var store = new ConversationStore(clock, TimeSpan.FromMinutes(3));

        store.Set("a", new TestPending());
        Assert.Null(store.Take("b"));
        Assert.IsType<TestPending>(store.Take("a"));
        Assert.Null(store.Take("a"));

        store.Set("a", new TestPending());
        clock.Now += TimeSpan.FromMinutes(3);
        Assert.Null(store.Take("a"));
    }

    [Fact]
    public async Task Every_step_writes_one_snake_case_line_sharing_the_request_id()
    {
        using var temp = new TempTrace();
        var pipeline = TestPipeline.Create(new ConversationStore(TimeProvider.System, TimeSpan.FromMinutes(3)), new ToolCatalog([]), temp.TraceLog);

        var reply = await pipeline.HandleAsync(new MessageRequest("hello", "test", "c1"), CancellationToken.None);

        var lines = temp.Lines();
        Assert.Equal(["request", "pending", "fast_path", "llm:route", "reply"], lines.Select(l => l.GetProperty("step").GetString()));
        Assert.All(lines, l => Assert.Equal(reply.RequestId, l.GetProperty("request_id").GetString()));
        Assert.Equal("hello", lines[0].GetProperty("input").GetString());
        Assert.False(lines[1].TryGetProperty("tool", out _)); // unset fields are omitted, not null
    }

    [Theory]
    [InlineData("", "test", "c1")]
    [InlineData("hi", "", "c1")]
    [InlineData("hi", "test", " ")]
    public void Blank_fields_are_rejected(string text, string channel, string conversationId) =>
        Assert.NotNull(new MessageRequest(text, channel, conversationId).Error());

    [Fact]
    public void Overlong_text_is_rejected() =>
        Assert.NotNull(new MessageRequest(new string('a', MessageRequest.MaxTextLength + 1), "test", "c1").Error());
}
