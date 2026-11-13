using System.Net;
using System.Text.Json;
using Ekko.Brain.Budget;
using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Tests;

public class BudgetTests
{
    private const string Verizon = """
        {"status":"found","message":"Found Verizon ($90 on Nov 20).",
         "candidates":[{"tab":"November","row":41,"item":"Verizon","amount":-90,"label":"Verizon ($90 on Nov 20)"}]}
        """;
    private const string Insurance = """
        {"status":"ambiguous","message":"Which one: Dental Insurance ($40 on Nov 3) or Car Insurance ($190 on Nov 28)?",
         "candidates":[{"tab":"November","row":15,"item":"Dental Insurance","amount":-40,"label":"Dental Insurance ($40 on Nov 3)"},
                       {"tab":"November","row":44,"item":"Car Insurance","amount":-190,"label":"Car Insurance ($190 on Nov 28)"}]}
        """;
    private const string Ok = """{"status":"ok","message":"Done on the sheet."}""";

    private readonly FakeHttp _budget = new();
    private readonly Queue<string> _answers = new();
    private readonly ToolCatalog _catalog;

    public BudgetTests()
    {
        _budget.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_answers.Dequeue()) };
        _catalog = new ToolCatalog(TestPipeline.BudgetTools(_budget));
    }

    private Task<ToolResult> Run(string tool, string args, bool confirmed = false) =>
        ToolGate.ExecuteAsync(_catalog.Find(tool)!, JsonDocument.Parse(args).RootElement, confirmed, CancellationToken.None);

    /// <summary>What the pipeline does on "yes": run the pending call through the gate, confirmed.</summary>
    private Task<ToolResult> Yes(ToolResult asked)
    {
        var confirm = Assert.IsType<PendingConfirm>(asked.Next);
        return ToolGate.ExecuteAsync(_catalog.Find(confirm.Tool)!, confirm.Args, confirmed: true, CancellationToken.None);
    }

    [Fact]
    public async Task One_matching_bill_is_read_back_exactly_and_written_only_after_yes()
    {
        _answers.Enqueue(Verizon);
        var asked = await Run("mark_bill_paid", """{"item":"verizon","amount":220}""");

        Assert.Equal("Check off Verizon ($90 on Nov 20) at $220 instead. Go ahead?", asked.Say);
        Assert.Equal("/budget/bills/find", _budget.Requests.Single().Path); // looked up, nothing written

        _answers.Enqueue(Ok);
        Assert.Equal("Done on the sheet.", (await Yes(asked)).Say);
        Assert.Equal("/budget/bills/paid", _budget.Requests[1].Path);
        Assert.Equal("""{"tab":"November","row":41,"item":"Verizon","amount":220}""", _budget.Requests[1].Body);
    }

    [Fact]
    public async Task Saying_the_planned_amount_isnt_a_change()
    {
        _answers.Enqueue(Verizon);
        var asked = await Run("mark_bill_paid", """{"item":"verizon","amount":90}""");
        Assert.Equal("Check off Verizon ($90 on Nov 20). Go ahead?", asked.Say);

        _answers.Enqueue(Ok);
        await Yes(asked);
        Assert.Equal("""{"tab":"November","row":41,"item":"Verizon"}""", _budget.Requests[1].Body);
    }

    [Fact]
    public async Task An_amount_alone_looks_the_row_up_and_isnt_read_back_as_a_change()
    {
        _answers.Enqueue(Verizon);
        var asked = await Run("mark_bill_paid", """{"amount":90}""");

        Assert.Equal("""{"amount":90}""", _budget.Requests.Single().Body);
        Assert.Equal("Check off Verizon ($90 on Nov 20). Go ahead?", asked.Say);
    }

    [Fact]
    public async Task Neither_a_name_nor_an_amount_asks_without_calling_the_service()
    {
        var result = await Run("mark_bill_paid", "{}");
        Assert.Equal("Which one? Say its name or its amount.", result.Say);
        Assert.Empty(_budget.Requests);
    }

    [Fact]
    public async Task Several_bills_become_a_choice_and_the_pick_still_asks_before_writing()
    {
        _answers.Enqueue(Insurance);
        var asked = await Run("mark_bill_paid", """{"item":"insurance"}""");

        var choice = Assert.IsType<PendingChoice>(asked.Next);
        Assert.Equal(MarkRowPaid.ToolName, choice.Tool);
        var picked = choice.Pick("the dental one")!;

        var confirm = await ToolGate.ExecuteAsync(_catalog.Find(choice.Tool)!, picked.Args, confirmed: false, CancellationToken.None);
        Assert.Equal("Check off Dental Insurance ($40 on Nov 3). Go ahead?", confirm.Say);
        Assert.Single(_budget.Requests);
    }

    /// <summary>Three rows with one name, read out by amount and date, like the deck's "which one" on Nov 2.</summary>
    private const string Streaming = """
        {"status":"ambiguous","message":"Which one: Streaming (Basic) ($12.47 on Nov 2), Streaming (Kids) ($3.49 on Nov 22) or Streaming (Sports) ($5.49 on Nov 23)?",
         "candidates":[{"tab":"November","row":10,"item":"Streaming (Basic)","amount":-12.47,"label":"Streaming (Basic) ($12.47 on Nov 2)"},
                       {"tab":"November","row":36,"item":"Streaming (Kids)","amount":-3.49,"label":"Streaming (Kids) ($3.49 on Nov 22)"},
                       {"tab":"November","row":37,"item":"Streaming (Sports)","amount":-5.49,"label":"Streaming (Sports) ($5.49 on Nov 23)"}]}
        """;

    [Theory]
    [InlineData("November 2", 0)]          // the date it was read out with, not "the second one"
    [InlineData("november 2nd", 0)]
    [InlineData("the one on the 22nd", 1)]
    [InlineData("12.47", 0)]               // the amount
    [InlineData("the $3.49 one", 1)]
    [InlineData("kids", 1)]
    [InlineData("2", 0)]                   // a day that was read out ("Nov 2") wins over "the second one"
    [InlineData("3", 2)]                   // no row on the 3rd: a number on its own is a position
    [InlineData("number 3", 2)]
    [InlineData("the second one", 1)]
    [InlineData("November 3", null)]       // no row on Nov 3: not "the third one"
    [InlineData("streaming", null)]        // names them all
    public async Task A_which_one_answer_matches_what_was_read_out(string answer, int? expected)
    {
        _answers.Enqueue(Streaming);
        var choice = Assert.IsType<PendingChoice>((await Run("mark_bill_paid", """{"item":"streaming"}""")).Next);
        Assert.Equal(expected is int i ? choice.Options[i] : null, choice.Pick(answer));
    }

    [Fact]
    public async Task No_matching_bill_is_a_failure_in_the_services_words()
    {
        _answers.Enqueue("""{"status":"not_found","message":"I couldn't find an unchecked rent on this month's or last month's tab."}""");
        var result = await Run("mark_bill_paid", """{"item":"rent"}""");
        Assert.False(result.Ok);
        Assert.Null(result.Next);
        Assert.StartsWith("I couldn't find", result.Say);
    }

    [Fact]
    public async Task An_expense_is_read_back_and_sent_only_after_yes()
    {
        var asked = await Run("add_expense", """{"item":"new tire","amount":380}""");
        Assert.Equal("Add new tire, $380 spent, marked unexpected. Go ahead?", asked.Say);
        Assert.Empty(_budget.Requests);

        _answers.Enqueue(Ok);
        await Yes(asked);
        Assert.Equal("/budget/expenses", _budget.Requests.Single().Path);
        Assert.Equal("""{"item":"new tire","amount":380,"income":false,"planned":false}""", _budget.Requests[0].Body);
    }

    [Fact]
    public async Task Money_coming_in_reads_back_as_income() =>
        Assert.Equal("Add refund, $50.25 coming in. Go ahead?", (await Run("add_expense", """{"item":"refund","amount":50.25,"income":true}""")).Say);

    [Fact]
    public async Task Status_asks_for_the_named_month()
    {
        _answers.Enqueue(Ok);
        await Run("get_budget_status", """{"month":"october"}""");
        Assert.Equal("/budget/status?month=October", _budget.Requests.Single().Path);
    }

    [Theory]
    [InlineData("""{"when":"yesterday"}""", "/budget/bills?when=yesterday")]
    [InlineData("""{"when":"tomorrow"}""", "/budget/bills?when=tomorrow")]
    [InlineData("""{}""", "/budget/bills?when=week")]
    public async Task Bills_ask_for_the_day_said_and_the_week_by_default(string args, string path)
    {
        _answers.Enqueue(Ok);
        await Run("get_bills", args);
        Assert.Equal(path, _budget.Requests.Single().Path);
    }

    [Fact]
    public async Task Money_left_is_read_by_the_service()
    {
        _answers.Enqueue(Ok);
        Assert.Equal("Done on the sheet.", (await Run("get_money_left", "{}")).Say);
        Assert.Equal("/budget/left", _budget.Requests.Single().Path);
    }

    [Fact]
    public void The_model_never_sees_the_row_writer() =>
        Assert.DoesNotContain(_catalog.ForDomain(Domain.Budget), t => t.Name == MarkRowPaid.ToolName);

    [Fact]
    public async Task A_row_write_without_a_yes_is_held_by_the_gate()
    {
        var result = await Run(MarkRowPaid.ToolName, """{"tab":"November","row":41,"item":"Verizon","label":"Verizon ($90 on Nov 20)"}""");
        Assert.IsType<PendingConfirm>(result.Next);
        Assert.Empty(_budget.Requests);
    }
}
