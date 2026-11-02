using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ekko.Brain.Media;
using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Tests;

public class MediaTests
{
    private const string MovieId = "8330c722accb2e1515683f1b834f366b";
    private const string SeriesId = "11112222333344445555666677778888";

    private static readonly string Ambiguous = $$$"""
        {"status":"ambiguous","message":"Which one: Scooby-Doo (2002) or Scooby-Doo, Where Are You! (1969, series)?",
         "data":{"candidates":[
           {"id":"{{{MovieId}}}","name":"Scooby-Doo","type":"Movie","productionYear":2002,"label":"Scooby-Doo (2002)"},
           {"id":"{{{SeriesId}}}","name":"Scooby-Doo, Where Are You!","type":"Series","productionYear":1969,"label":"Scooby-Doo, Where Are You! (1969, series)"}]}}
        """;
    private const string Playing = """{"status":"playing","message":"Playing Scooby-Doo (2002).","data":{"item":{}}}""";

    private readonly FakeHttp _tv = new();
    private readonly Queue<string> _answers = new();

    public MediaTests() => _tv.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_answers.Dequeue()) };

    private ToolCatalog Catalog() => new(TestPipeline.MediaTools(_tv));

    private Task<ToolResult> Run(string tool, string args) =>
        ToolGate.ExecuteAsync(Catalog().Find(tool)!, JsonDocument.Parse(args).RootElement, confirmed: false, CancellationToken.None);

    [Fact]
    public async Task Play_sends_only_the_fields_given_and_reads_back_tvremotes_sentence()
    {
        _answers.Enqueue(Playing);
        var result = await Run("play_media", """{"title":"Scooby-Doo","kind":"movie"}""");

        Assert.Equal("Playing Scooby-Doo (2002).", result.Say);
        var sent = _tv.Requests.Single();
        Assert.Equal("/tv/play", sent.Path);
        Assert.Equal("""{"title":"Scooby-Doo","kind":"movie"}""", sent.Body); // no nulls for season/episode
    }

    [Fact]
    public async Task Several_candidates_become_a_pending_choice_played_by_id()
    {
        _answers.Enqueue(Ambiguous);
        var result = await Run("play_media", """{"title":"Scooby-Doo"}""");

        var choice = Assert.IsType<PendingChoice>(result.Next);
        Assert.Equal(PlayMediaItem.ToolName, choice.Tool);
        Assert.Equal(MovieId, choice.Options[0].Args.GetProperty("item_id").GetString());
        Assert.StartsWith("Which one", result.Say);
    }

    [Fact]
    public async Task Nothing_found_is_a_failure_with_tvremotes_words()
    {
        _answers.Enqueue("""{"status":"not_found","message":"I couldn't find \"Zzz\" in your library.","data":{"candidates":[]}}""");
        var result = await Run("play_media", """{"title":"Zzz"}""");
        Assert.False(result.Ok);
        Assert.Null(result.Next);
        Assert.Contains("couldn't find", result.Say);
    }

    [Fact]
    public async Task Tvremote_down_is_a_sentence_not_an_exception()
    {
        _tv.Respond = _ => throw new HttpRequestException("refused");
        Assert.Equal("The TV remote isn't answering.", (await Run("media_control", """{"action":"pause"}""")).Say);
    }

    [Fact]
    public async Task Control_maps_to_tvremotes_routes()
    {
        _answers.Enqueue("""{"status":"ok","message":"Resuming."}""");
        Assert.Equal("Resuming.", (await Run("media_control", """{"action":"resume"}""")).Say);
        Assert.Equal("/tv/resume", _tv.Requests.Single().Path);
    }

    [Fact]
    public void The_model_is_never_offered_the_by_id_tool()
    {
        var offered = Catalog().ForDomain(Domain.Media).Select(t => t.Name).ToList();
        Assert.Equal(["play_media", "media_control"], offered);
        Assert.NotNull(Catalog().Find(PlayMediaItem.ToolName)); // still reachable from code, through the gate
    }

    [Theory]
    [InlineData("the movie", 0)]
    [InlineData("The show!", 1)]
    [InlineData("the 2002 one", 0)]
    [InlineData("where are you", 1)]
    [InlineData("the second one", 1)]
    [InlineData("first", 0)]
    [InlineData("the last one", 1)]
    [InlineData("scooby doo", null)]   // names both: not a choice
    [InlineData("mute discord", null)] // a new request
    public void Answers_pick_a_candidate_in_code(string answer, int? expected)
    {
        _answers.Enqueue(Ambiguous);
        var choice = (PendingChoice)Run("play_media", """{"title":"Scooby-Doo"}""").Result.Next!;
        var picked = choice.Pick(answer);
        Assert.Equal(expected is int i ? choice.Options[i] : null, picked);
    }

    [Fact]
    public async Task The_spec_example_end_to_end()
    {
        // "I'm really looking forward to Scooby-Doo" -> media -> play_media -> two candidates -> "the movie" -> plays by id.
        using var trace = new TempTrace();
        var store = new ConversationStore(TimeProvider.System, TimeSpan.FromMinutes(3));
        var llm = new ScriptedLlm("qwen", """{"domain":"media","confidence":"high"}""", """{"tool":"play_media","args":{"title":"Scooby-Doo"}}""");
        _answers.Enqueue(Ambiguous);
        _answers.Enqueue(Playing);

        var first = await TestPipeline.Create(store, Catalog(), trace.TraceLog, llm)
            .HandleAsync(new MessageRequest("Hey Ekko, I'm really looking forward to Scooby-Doo", "test", "c1"), default);
        Assert.StartsWith("Which one", first.Text);

        var second = await TestPipeline.Create(store, Catalog(), trace.TraceLog, llm)
            .HandleAsync(new MessageRequest("the movie", "test", "c1"), default);
        Assert.Equal("Playing Scooby-Doo (2002).", second.Text);
        Assert.Equal(2, llm.Requests.Count); // the answer needed no model call
        Assert.Equal(MovieId, JsonNode.Parse(_tv.Requests[1].Body!)!["itemId"]!.GetValue<string>());
    }
}
