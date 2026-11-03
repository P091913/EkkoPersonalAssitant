using System.Net;
using System.Text.Json;
using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Tests;

public class FoodTests : IDisposable
{
    private const string Unknown = """{"status":"unknown","message":"big mac isn't on your food list yet.","unknown":["big mac"]}""";
    private const string Logged = """{"status":"ok","message":"Logged it."}""";

    private readonly FakeHttp _food = new();
    private readonly Queue<string> _answers = new();
    private readonly TempTrace _trace = new();

    public FoodTests() =>
        _food.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_answers.Dequeue()) };

    public void Dispose() => _trace.Dispose();

    private ToolCatalog Catalog(ScriptedLlm? estimate = null) => new(TestPipeline.FoodTools(_food, _trace.TraceLog, estimate));

    private static Task<ToolResult> Run(ToolCatalog catalog, string tool, string args, bool confirmed = false) =>
        ToolGate.ExecuteAsync(catalog.Find(tool)!, JsonDocument.Parse(args).RootElement, confirmed, CancellationToken.None);

    private static Task<ToolResult> Yes(ToolCatalog catalog, ToolResult asked)
    {
        var confirm = Assert.IsType<PendingConfirm>(asked.Next);
        return ToolGate.ExecuteAsync(catalog.Find(confirm.Tool)!, confirm.Args, confirmed: true, CancellationToken.None);
    }

    private JsonElement Sent(int n) => JsonDocument.Parse(_food.Requests[n].Body!).RootElement;

    [Fact]
    public async Task Foods_on_the_list_log_straight_away_without_a_model()
    {
        var estimate = new ScriptedLlm("sonnet", """{"calories":70}""");
        _answers.Enqueue("""{"status":"ok","message":"Logged 2 eggs and toast: 220 calories. 220 calories today."}""");

        var result = await Run(Catalog(estimate), "log_food", """{"items":[{"food":"egg","quantity":2},{"food":"toast"}]}""");

        Assert.Equal("Logged 2 eggs and toast: 220 calories. 220 calories today.", result.Say);
        Assert.Null(result.Next);
        Assert.Empty(estimate.Requests);
        Assert.Equal("/food/log", _food.Requests.Single().Path);
        Assert.Equal("""[{"food":"egg","quantity":2,"save":false},{"food":"toast","quantity":1,"save":false}]""", Sent(0).GetProperty("items").GetRawText());
    }

    [Fact]
    public async Task A_new_food_is_estimated_read_back_and_saved_only_on_yes()
    {
        var estimate = new ScriptedLlm("sonnet", """{"calories":590}""");
        var catalog = Catalog(estimate);
        _answers.Enqueue("""{"status":"unknown","message":"big mac isn't on your food list yet.","unknown":["big mac"]}""");

        var asked = await Run(catalog, "log_food", """{"items":[{"food":"big mac"},{"food":"egg","quantity":2}]}""");

        Assert.Equal("New food: big mac at 590 calories each. Log big mac and 2 eggs. Go ahead?", asked.Say);
        Assert.Equal("big mac", estimate.Requests.Single().Messages.Single().Content);
        Assert.Single(_food.Requests); // nothing logged yet

        _answers.Enqueue(Logged);
        Assert.Equal("Logged it.", (await Yes(catalog, asked)).Say);
        Assert.Equal("""[{"food":"big mac","quantity":1,"caloriesEach":590,"save":true},{"food":"egg","quantity":2,"save":false}]""",
            Sent(1).GetProperty("items").GetRawText());
    }

    [Fact]
    public async Task A_calorie_number_the_user_said_is_read_back_instead_of_an_estimate()
    {
        var estimate = new ScriptedLlm("sonnet", """{"calories":590}""");
        _answers.Enqueue(Unknown);

        // "2 big macs, 1100 calories": the number is for both.
        var asked = await Run(Catalog(estimate), "log_food", """{"items":[{"food":"big mac","quantity":2,"calories":1100}]}""");

        Assert.Equal("New food: big mac at 550 calories each. Log 2 big macs. Go ahead?", asked.Say);
        Assert.Empty(estimate.Requests);
    }

    [Fact]
    public async Task When_no_estimate_comes_back_Ekko_asks_for_the_number_and_logs_nothing()
    {
        _answers.Enqueue(Unknown);
        var result = await Run(Catalog(), "log_food", """{"items":[{"food":"big mac"}]}""");

        Assert.False(result.Ok);
        Assert.Equal("I couldn't estimate big mac. How many calories was it?", result.Say);
        Assert.Single(_food.Requests);
    }

    [Theory]
    [InlineData("""{"items":[]}""")]
    [InlineData("""{"items":[{"food":"egg","quantity":0}]}""")]
    [InlineData("""{"items":[{"food":""}]}""")]
    [InlineData("""{"items":[{"food":"egg","grams":200}]}""")]
    public async Task Each_item_is_checked_before_anything_is_sent(string args)
    {
        var result = await Run(Catalog(), "log_food", args);
        Assert.False(result.Ok);
        Assert.Empty(_food.Requests);
    }

    [Fact]
    public async Task Changing_a_saved_food_reads_back_the_number_it_replaces()
    {
        var catalog = Catalog();
        _answers.Enqueue("""{"status":"found","message":"big mac is 590 calories each.","name":"big mac","caloriesEach":590}""");
        var asked = await Run(catalog, "set_food_calories", """{"food":"Big Macs","calories":560}""");
        Assert.Equal("Change big mac from 590 calories to 560 calories each. Go ahead?", asked.Say);
        Assert.Equal("/food/foods?food=Big%20Macs", _food.Requests.Single().Path);

        _answers.Enqueue("""{"status":"ok","message":"big mac is now 560 calories each."}""");
        Assert.Equal("big mac is now 560 calories each.", (await Yes(catalog, asked)).Say);
        Assert.Equal("""{"food":"big mac","caloriesEach":560}""", _food.Requests[1].Body);
    }

    [Fact]
    public async Task A_change_to_a_food_not_on_the_list_is_read_back_as_a_new_food_to_log()
    {
        // "more like 560" after "New food: big mac at 590 calories each. Log big mac. Go ahead?", taken for a change:
        // big mac isn't saved yet, so a yes must log it, not quietly save a number.
        var catalog = Catalog();
        _answers.Enqueue(Unknown);
        var asked = await Run(catalog, "set_food_calories", """{"food":"big mac","calories":560}""");
        Assert.Equal("New food: big mac at 560 calories each. Log big mac. Go ahead?", asked.Say);

        _answers.Enqueue(Logged);
        await Yes(catalog, asked);
        Assert.Equal("""[{"food":"big mac","quantity":1,"caloriesEach":560,"save":true}]""", Sent(1).GetProperty("items").GetRawText());
    }

    [Fact]
    public async Task The_diary_and_undo_are_one_call_each()
    {
        var catalog = Catalog();
        _answers.Enqueue("""{"status":"empty","message":"Nothing logged yesterday."}""");
        _answers.Enqueue("""{"status":"empty","message":"Nothing's logged today to undo."}""");

        Assert.Equal("Nothing logged yesterday.", (await Run(catalog, "get_food_log", """{"day":"yesterday"}""")).Say);
        Assert.True((await Run(catalog, "undo_food", "{}")).Ok);
        Assert.Equal(["/food/day?day=yesterday", "/food/undo"], _food.Requests.Select(r => r.Path));
    }

    [Theory]
    [InlineData("half a turkey sandwich", 0.5, "turkey sandwich", 0.5)]
    [InlineData("half a turkey sandwich", 1, "turkey sandwich", 0.5)]
    [InlineData("Half of an apple", 1, "apple", 0.5)]
    [InlineData("half and half", 1, "half and half", 1)]
    [InlineData("half", 1, "half", 1)]
    [InlineData("turkey sandwich", 0.5, "turkey sandwich", 0.5)]
    public void Half_left_in_the_food_is_the_quantity(string food, double quantity, string expectedFood, double expectedQuantity)
    {
        var entry = Ekko.Brain.Food.LogFood.Entry(new Ekko.Brain.Food.FoodItem(food, quantity));
        Assert.Equal((expectedFood, expectedQuantity), (entry.Food, entry.Quantity));
    }

    [Fact]
    public void The_model_never_sees_the_tools_that_write() =>
        Assert.Equal(["get_food_log", "log_food", "set_food_calories", "undo_food"], Catalog().ForDomain(Domain.Food).Select(t => t.Name).Order());
}
