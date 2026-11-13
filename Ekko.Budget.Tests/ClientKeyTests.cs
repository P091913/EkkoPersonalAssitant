using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ekko.Budget.Tests;

/// <summary>The real CORS, key and permission checks on loopback, with stand-in endpoints instead of the sheet (DR-45, DR-46).</summary>
public sealed class ClientKeyTests : IAsyncLifetime
{
    private const string Brain = "brain-test-key", Desk = "desk-test-key", Glasses = "glasses-test-key";
    private const string Tick = """{"tab":"November","row":33,"item":"Cloaked"}""";
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddCors();
        _app = builder.Build();
        // As compose.yaml sets them: the Brain unrestricted, the desk read-only, the glasses reading and checking bills off.
        _app.UseClientKeys(new Dictionary<string, string> { ["brain"] = Brain, ["desk"] = Desk, ["glasses"] = Glasses },
            ["desk"], new Dictionary<string, string[]> { ["glasses"] = ["POST /budget/bills/paid"] });
        _app.MapGet("/budget/glance", () => "glance");
        _app.MapPost("/budget/expenses", () => "added");
        _app.MapPost("/budget/bills/find", () => "found");
        _app.MapPost("/budget/bills/paid", (PaidRequest r) => r); // echoes what bound: the body is still readable after the check
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? key, string? json = null, params (string, string)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", "http://example.com");
        if (key is not null) request.Headers.Add("X-Api-Key", key);
        foreach (var (name, value) in headers) request.Headers.Add(name, value);
        if (json is not null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return _http.SendAsync(request);
    }

    private async Task<(HttpStatusCode, string)> Post(string path, string key, string json = Tick)
    {
        var res = await Send(HttpMethod.Post, path, key, json);
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", "x-api-key")]
    [InlineData("POST", "content-type,x-api-key")]
    public async Task A_preflight_without_a_key_is_allowed(string method, string headers)
    {
        var res = await Send(HttpMethod.Options, "/budget/bills/paid", null, null,
            ("Access-Control-Request-Method", method), ("Access-Control-Request-Headers", headers));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal("*", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains(method, res.Headers.GetValues("Access-Control-Allow-Methods").Single().Split(','));
    }

    [Fact]
    public async Task The_glasses_key_reads_the_glance()
    {
        var res = await Send(HttpMethod.Get, "/budget/glance", Glasses);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("*", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task The_glasses_key_checks_a_bill_off()
    {
        var res = await Send(HttpMethod.Post, "/budget/bills/paid", Glasses, Tick);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(new PaidRequest("November", 33, "Cloaked", null), await res.Content.ReadFromJsonAsync<PaidRequest>());
    }

    [Theory]
    [InlineData("""{"tab":"November","row":33,"item":"Cloaked","amount":20}""")]
    [InlineData("""{"tab":"November","row":33,"item":"Cloaked","Amount":20}""")] // binds just the same, so it's refused just the same
    public async Task The_glasses_key_cannot_change_what_a_bill_came_to(string json)
    {
        var (status, body) = await Post("/budget/bills/paid", Glasses, json);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("not change what it came to", body);
    }

    [Theory]
    [InlineData("/budget/expenses")]
    [InlineData("/budget/bills/find")]
    [InlineData("/budget/BILLS/paid")] // another spelling of an allowed write isn't the allowed write
    public async Task The_glasses_key_makes_no_other_write(string path) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(path, Glasses)).Item1);

    [Fact]
    public async Task The_desk_key_only_reads() => Assert.Equal(HttpStatusCode.Forbidden, (await Post("/budget/bills/paid", Desk)).Item1);

    [Theory]
    [InlineData("/budget/bills/paid", """{"tab":"November","row":33,"item":"Cloaked","amount":20}""")]
    [InlineData("/budget/expenses", "{}")]
    [InlineData("/budget/bills/find", "{}")]
    public async Task The_brain_key_does_anything(string path, string json) =>
        Assert.Equal(HttpStatusCode.OK, (await Post(path, Brain, json)).Item1);

    [Fact]
    public async Task CORS_does_not_open_the_door_without_a_key()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/budget/glance", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/budget/glance", "wrong")).StatusCode);
    }
}
