using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Tests;

public class MemoryTests
{
    private readonly ManualClock _clock = new();
    private readonly ConversationMemory _memory;

    public MemoryTests() => _memory = new ConversationMemory(_clock, new ConversationOptions());

    [Fact]
    public void Turns_belong_to_their_conversation_and_end_with_five_minutes_of_silence()
    {
        _memory.Remember("deck", "what bills are left", "Three: water, gym and kids.");
        Assert.Equal([new Turn("what bills are left", "Three: water, gym and kids.")], _memory.Recall("deck").Turns);
        Assert.Empty(_memory.Recall("phone").Turns);

        _clock.Now += TimeSpan.FromMinutes(4);
        Assert.Single(_memory.Recall("deck").Turns); // still the same session

        _clock.Now += TimeSpan.FromMinutes(5.5);
        Assert.Same(History.None, _memory.Recall("deck"));
        _memory.Remember("deck", "volume up", "Volume up.");
        Assert.Equal("volume up", _memory.Recall("deck").Turns.Single().User); // a new session starts clean
    }

    [Fact]
    public void The_sixth_message_of_a_session_is_deep_until_the_session_ends()
    {
        for (var i = 1; i <= 4; i++)
            _memory.Remember("deck", $"message {i}", "ok");
        Assert.False(_memory.Recall("deck").Deep); // the 5th message
        _memory.Remember("deck", "message 5", "ok");
        Assert.True(_memory.Recall("deck").Deep);  // the 6th

        _clock.Now += TimeSpan.FromMinutes(6);
        Assert.False(_memory.Recall("deck").Deep);
    }

    [Fact]
    public void Only_the_newest_turns_that_fit_are_recalled()
    {
        var memory = new ConversationMemory(_clock, new ConversationOptions { MaxHistoryChars = 50 });
        memory.Remember("deck", "first question here", "first answer here");   // 36 characters
        memory.Remember("deck", "second question", "second answer");          // 28: both together don't fit
        Assert.Equal("second question", memory.Recall("deck").Turns.Single().User);
    }

    [Fact]
    public void Long_replies_are_trimmed_before_they_are_kept()
    {
        _memory.Remember("deck", "how's the budget", new string('x', 2000));
        Assert.Equal(601, _memory.Recall("deck").Turns.Single().Ekko.Length); // 600 + "…"
    }

    [Fact]
    public void Without_earlier_turns_a_step_sends_exactly_the_message()
    {
        Assert.Equal([new("user", "volume up")], History.None.Messages("volume up"));
        var messages = new History([new Turn("volume down", "Volume down.")], false).Messages("put it back up");
        Assert.Equal("put it back up", messages[^1].Content); // the latest stays its own message
        Assert.Contains("User: volume down\nEkko: Volume down.", messages[0].Content);
    }
}

public class FollowUpTests
{
    private const string DesktopHigh = """{"domain":"desktop","confidence":"high"}""";
    private const string VolumeUp = """{"tool":"adjust_volume","args":{"direction":"up"}}""";

    private readonly FakeHttp _pc = new();
    private readonly TempTrace _trace = new();
    private readonly ConversationStore _store = new(TimeProvider.System, TimeSpan.FromMinutes(3));
    private readonly ManualClock _clock = new();
    private readonly ConversationMemory _memory;

    public FollowUpTests() => _memory = new ConversationMemory(_clock, new ConversationOptions());

    private async Task<string> Send(string text, ScriptedLlm? qwen = null, ScriptedLlm? deep = null) =>
        (await TestPipeline.Create(_store, new ToolCatalog(TestPipeline.DesktopTools(_pc)), _trace.TraceLog, qwen, memory: _memory, deep: deep)
            .HandleAsync(new MessageRequest(text, "test", "c1"), CancellationToken.None)).Text;

    [Fact]
    public async Task A_follow_up_is_routed_and_extracted_with_the_earlier_turns()
    {
        Assert.Equal("Volume down.", await Send("volume down")); // fast path, still remembered

        var qwen = new ScriptedLlm("qwen", DesktopHigh, VolumeUp);
        Assert.Equal("Volume up.", await Send("actually put it back up", qwen));

        foreach (var request in qwen.Requests) // route, then extract
        {
            Assert.Equal(2, request.Messages.Count);
            Assert.Contains("User: volume down\nEkko: Volume down.", request.Messages[0].Content);
            Assert.Equal("actually put it back up", request.Messages[1].Content);
        }
        Assert.Equal(1, _trace.Lines().Last(l => l.GetProperty("step").GetString() == "request").GetProperty("turns").GetInt32());
    }

    [Fact]
    public async Task From_the_sixth_message_the_deep_model_answers_until_five_quiet_minutes()
    {
        for (var i = 0; i < 5; i++)
            await Send("volume up");

        var qwen = new ScriptedLlm("qwen", DesktopHigh, VolumeUp);
        var deep = new ScriptedLlm("haiku", DesktopHigh, VolumeUp);
        Assert.Equal("Volume up.", await Send("crank it a bit", qwen, deep));
        Assert.Empty(qwen.Requests);
        Assert.Equal(2, deep.Requests.Count);
        var modelLines = _trace.Lines().Where(l => l.GetProperty("step").GetString()!.StartsWith("llm:")).ToList();
        Assert.All(modelLines, l => Assert.True(l.GetProperty("deep").GetBoolean()));

        _clock.Now += TimeSpan.FromMinutes(6);
        var qwenAgain = new ScriptedLlm("qwen", DesktopHigh, VolumeUp);
        var unused = new ScriptedLlm("haiku");
        await Send("crank it a bit", qwenAgain, unused);
        Assert.Equal(2, qwenAgain.Requests.Count);
        Assert.Empty(unused.Requests);
        Assert.Single(qwenAgain.Requests[0].Messages); // a new session: no earlier turns
    }

    [Fact]
    public async Task A_deep_session_still_answers_when_Claude_cant_be_reached()
    {
        for (var i = 0; i < 5; i++)
            await Send("volume up");

        var qwen = new ScriptedLlm("qwen", DesktopHigh, VolumeUp);
        Assert.Equal("Volume up.", await Send("crank it a bit", qwen, deep: new ScriptedLlm("haiku", [null])));
        Assert.Equal(2, qwen.Requests.Count); // Qwen, the step's Primary, is the fallback
    }
}
