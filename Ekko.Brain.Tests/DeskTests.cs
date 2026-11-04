using System.Net;
using System.Text.Json;
using Ekko.Brain.Desk;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Tests;

public class DeskTests
{
    private readonly FakeHttp _desk = new();

    private ToolCatalog Catalog() => new([new AddDeskNote(new DeskService(_desk.Client("http://desk/")))]);

    private Task<ToolResult> Note(string args) =>
        ToolGate.ExecuteAsync(Catalog().Find("add_desk_note")!, JsonDocument.Parse(args).RootElement, confirmed: false, CancellationToken.None);

    private void Answer(HttpStatusCode status, string body) =>
        _desk.Respond = _ => new HttpResponseMessage(status) { Content = new StringContent(body) };

    [Fact]
    public async Task A_note_goes_on_the_desk_at_once_and_the_desk_words_the_reply()
    {
        Answer(HttpStatusCode.Created, """{"ok":true,"id":"note-7","message":"Put “call mom” on your desk."}""");

        var result = await Note("""{"text":"  call mom "}""");

        Assert.True(result.Ok);
        Assert.Null(result.Next); // Reversible: no read-back first
        Assert.Equal("Put “call mom” on your desk.", result.Say);
        var sent = _desk.Requests.Single();
        Assert.Equal((HttpMethod.Post, "/api/notes"), (sent.Method, sent.Path));
        Assert.Equal("""{"text":"call mom"}""", sent.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"ok":false,"error":"Your desk already has 30 notes. Take some off first."}""", "Your desk already has 30 notes. Take some off first.")]
    [InlineData(HttpStatusCode.Unauthorized, """{"ok":false,"error":"missing or wrong X-Api-Key"}""", "Your desk refused Ekko's key.")]
    [InlineData(HttpStatusCode.Forbidden, """{"ok":false,"error":"forbidden"}""", "Your desk refused Ekko's key.")]
    [InlineData(HttpStatusCode.OK, "<html>", "Your desk gave an answer I couldn't read.")]
    public async Task What_the_desk_refuses_is_said_plainly(HttpStatusCode status, string body, string say)
    {
        Answer(status, body);

        var result = await Note("""{"text":"call mom"}""");

        Assert.False(result.Ok);
        Assert.Equal(say, result.Say);
    }

    [Fact]
    public async Task A_desk_that_is_off_is_said_plainly()
    {
        _desk.Respond = _ => throw new HttpRequestException("connection refused");

        Assert.Equal("Your desk isn't answering.", (await Note("""{"text":"call mom"}""")).Say);
    }

    [Fact]
    public async Task A_note_too_long_for_the_desk_or_blank_never_leaves_Ekko()
    {
        Assert.Equal("I can't do that: a desk note is limited to 200 characters.", (await Note($$"""{"text":"{{new string('a', 201)}}"}""")).Say);
        Assert.Equal("I can't do that: the note has no words.", (await Note("""{"text":"   "}""")).Say);
        Assert.Empty(_desk.Requests);
    }

    [Fact]
    public void The_desk_domain_offers_only_adding_a_note() =>
        Assert.Equal(["add_desk_note"], Catalog().ForDomain(Domain.Desk).Select(t => t.Name));
}
