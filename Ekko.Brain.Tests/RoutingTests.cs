using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Tests;

[RiskClass(Risk.Reversible)]
public sealed class FakeMediaTool() : Tool<NoArgs>("media_thing", Domain.Media, "A media tool.")
{
    protected override Task<ToolResult> RunAsync(NoArgs args, CancellationToken ct) => Task.FromResult(ToolResult.Done("media ran"));
}

public class RoutingTests
{
    private const string DesktopHigh = """{"domain":"desktop","confidence":"high"}""";
    private readonly FakeHttp _pc = new();
    private readonly TempTrace _trace = new();
    private readonly ConversationStore _store = new(TimeProvider.System, TimeSpan.FromMinutes(3));

    private async Task<string> Send(ScriptedLlm llm, string text) =>
        (await TestPipeline.Create(_store, new ToolCatalog([.. TestPipeline.DesktopTools(_pc), new FakeMediaTool()]), _trace.TraceLog, llm)
            .HandleAsync(new MessageRequest(text, "test", "c1"), CancellationToken.None)).Text;

    private List<string?> Steps() => _trace.Lines().Select(l => l.GetProperty("step").GetString()).ToList();

    [Fact]
    public async Task A_routed_command_is_extracted_and_run_through_the_gate()
    {
        var llm = new ScriptedLlm("qwen", DesktopHigh, """{"tool":"adjust_volume","args":{"direction":"up","steps":2}}""");

        Assert.Equal("Volume up, 2 notches.", await Send(llm, "crank it up a bit"));
        Assert.Equal(2, _pc.Requests.Count);
        Assert.Equal(["request", "pending", "fast_path", "llm:route", "route", "llm:extract", "extract", "tool", "reply"], Steps());
    }

    [Fact]
    public async Task None_means_no_tool_runs()
    {
        var llm = new ScriptedLlm("qwen", DesktopHigh, """{"tool":"none","args":{}}""");
        Assert.Equal("I can't do that yet.", await Send(llm, "close discord"));
        Assert.Empty(_pc.Requests);
    }

    [Fact]
    public async Task An_unbuilt_domain_says_not_yet_without_extracting()
    {
        var llm = new ScriptedLlm("qwen", """{"domain":"budget","confidence":"high"}""");
        Assert.Equal("I can't help with your budget yet.", await Send(llm, "how much is left for groceries"));
        Assert.Single(llm.Requests);
    }

    [Fact]
    public async Task Low_confidence_asks_and_a_yes_runs_the_original_command()
    {
        Assert.Equal("Is that about the PC?", await Send(new ScriptedLlm("qwen", """{"domain":"desktop","confidence":"low"}"""), "make it louder in here"));

        var extract = new ScriptedLlm("qwen", """{"tool":"adjust_volume","args":{"direction":"up"}}""");
        Assert.Equal("Volume up.", await Send(extract, "yes"));
        Assert.Equal("make it louder in here", extract.Requests.Single().Messages.Single().Content); // not "yes"
    }

    [Fact]
    public async Task Low_confidence_on_an_unbuilt_domain_doesnt_ask()
    {
        var llm = new ScriptedLlm("qwen", """{"domain":"calendar","confidence":"low"}""");
        Assert.Equal("I can't help with your calendar yet.", await Send(llm, "am I free"));
        Assert.Null(_store.Take("c1"));
    }

    [Fact]
    public async Task Extraction_only_accepts_the_routed_domains_tools()
    {
        // media_thing exists in the catalog, but the request was routed to desktop: rejected, retried with the reason.
        var llm = new ScriptedLlm("qwen", DesktopHigh, """{"tool":"media_thing","args":{}}""", """{"tool":"lock_pc","args":{}}""");

        Assert.Equal("Locking the PC.", await Send(llm, "lock it"));
        Assert.Contains("isn't one of", llm.Requests[2].Messages[^1].Content);
    }

    [Fact]
    public async Task An_allowlist_refusal_is_not_treated_as_a_model_error()
    {
        var llm = new ScriptedLlm("qwen", DesktopHigh, """{"tool":"open_app","args":{"app":"discord"}}""");

        Assert.Contains("allowlist", await Send(llm, "open discord"));
        Assert.Equal(2, llm.Requests.Count); // route + extract, no retry
        Assert.Empty(_pc.Requests);
    }

    [Fact]
    public void Extraction_schema_offers_each_tool_and_none()
    {
        var schema = DomainHandler.Schema(TestPipeline.DesktopTools(_pc)).ToJsonString();
        foreach (var name in new[] { "open_app", "adjust_volume", "lock_pc", "none" })
            Assert.Contains($"\"const\":\"{name}\"", schema);
        Assert.DoesNotContain("media_thing", schema);
    }
}
