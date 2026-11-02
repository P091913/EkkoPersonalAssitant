using System.Net;
using System.Text.Json.Nodes;
using Ekko.Brain.Llm;
using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Tests;

/// <summary>Answers from a script; a null entry throws <see cref="LlmException"/> (unreachable, timed out...).</summary>
public sealed class ScriptedLlm(string model, params string?[] answers) : ILlmClient
{
    public List<LlmRequest> Requests { get; } = [];
    public string Model => model;

    public Task<LlmResult> CompleteJsonAsync(LlmRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var answer = answers[Math.Min(Requests.Count, answers.Length) - 1];
        return answer is null
            ? throw new LlmException("unreachable")
            : Task.FromResult(new LlmResult(answer, model, 10, 5));
    }
}

public class StructuredCallTests
{
    private static readonly LlmRequest Request = new("system", [new LlmMessage("user", "go fast")], ToolJson.SchemaFor<SampleArgs>());
    private static readonly TraceEvent T = new("r1", "c1", "test");
    private readonly TempTrace _trace = new();

    private async Task<SampleArgs?> Ask(ScriptedLlm primary, ScriptedLlm? fallback)
    {
        var clients = new Dictionary<string, ILlmClient> { ["qwen"] = primary };
        if (fallback is not null)
            clients["haiku"] = fallback;
        var steps = new Dictionary<string, StepModels> { ["test"] = new() { Primary = "qwen", Fallback = "haiku" } };
        return await new StructuredCall(clients, steps, _trace.TraceLog)
            .AskAsync("test", Request, StructuredCall.Parse<SampleArgs>(), T, CancellationToken.None);
    }

    private List<int> Attempts() => _trace.Lines().Select(l => l.GetProperty("attempt").GetInt32()).ToList();

    [Fact]
    public async Task A_valid_first_answer_needs_one_call()
    {
        var fallback = new ScriptedLlm("haiku", """{"speed":"slow"}""");
        Assert.Equal(Speed.Slow, (await Ask(new ScriptedLlm("qwen", """{"speed":"slow"}"""), fallback))!.Speed);
        Assert.Empty(fallback.Requests);
        Assert.Equal([1], Attempts());
    }

    [Fact]
    public async Task An_invalid_answer_is_retried_once_with_the_error()
    {
        var primary = new ScriptedLlm("qwen", """{"speed":"warp"}""", """{"speed":"very_fast"}""");
        Assert.Equal(Speed.VeryFast, (await Ask(primary, null))!.Speed);

        var retry = primary.Requests[1].Messages;
        Assert.Equal(["user", "assistant", "user"], retry.Select(m => m.Role));
        Assert.Equal("""{"speed":"warp"}""", retry[1].Content);
        Assert.Contains("invalid", retry[2].Content);
        Assert.Equal([1, 2], Attempts());
        Assert.False(_trace.Lines()[0].GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Two_bad_answers_escalate_with_the_original_request()
    {
        var fallback = new ScriptedLlm("haiku", """{"speed":"slow"}""");
        Assert.NotNull(await Ask(new ScriptedLlm("qwen", "not json at all", """{"speed":"warp"}"""), fallback));
        Assert.Single(fallback.Requests[0].Messages); // clean context, not Qwen's mistakes
        Assert.Equal([1, 2, 3], Attempts());
    }

    [Fact]
    public async Task A_transport_failure_skips_the_retry_and_escalates()
    {
        var primary = new ScriptedLlm("qwen", [null]);
        Assert.NotNull(await Ask(primary, new ScriptedLlm("haiku", """{"speed":"slow"}""")));
        Assert.Single(primary.Requests);
        Assert.Equal([1, 3], Attempts());
    }

    [Fact]
    public async Task Without_a_fallback_it_gives_up() =>
        Assert.Null(await Ask(new ScriptedLlm("qwen", """{"speed":"warp"}"""), null));
}

public class LlmClientWireTests
{
    private static readonly LlmRequest Request = new("be brief", [new LlmMessage("user", "go fast")], ToolJson.SchemaFor<SampleArgs>());
    private readonly FakeHttp _http = new();

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    [Fact]
    public async Task OpenAi_compat_sends_a_json_schema_and_reads_content_and_usage()
    {
        _http.Respond = _ => Ok("""{"choices":[{"message":{"content":"{\"speed\":\"slow\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":42,"completion_tokens":7}}""");
        var result = await new OpenAiCompatClient(_http.Client("http://ollama/v1/"), "qwen3.5:9b", "none").CompleteJsonAsync(Request, default);

        Assert.Equal(("""{"speed":"slow"}""", 42, 7), (result.Json, result.TokensIn, result.TokensOut));
        var sent = _http.Requests.Single();
        Assert.Equal("/v1/chat/completions", sent.Path);
        var body = JsonNode.Parse(sent.Body!)!;
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("json_schema", body["response_format"]!["type"]!.GetValue<string>());
        Assert.NotNull(body["response_format"]!["json_schema"]!["schema"]!["properties"]!["speed"]);
        Assert.Equal("none", body["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task Anthropic_sends_headers_and_a_supported_schema_and_reads_the_text_block()
    {
        _http.Respond = _ => Ok("""
            {"content":[{"type":"thinking","thinking":""},{"type":"text","text":"{\"speed\":\"slow\"}"}],
             "stop_reason":"end_turn","usage":{"input_tokens":30,"output_tokens":9}}
            """);
        var result = await new AnthropicClient(_http.Client("https://api/v1/"), "claude-haiku-5-5", "test-key", "low").CompleteJsonAsync(Request, default);

        Assert.Equal(("""{"speed":"slow"}""", 30, 9), (result.Json, result.TokensIn, result.TokensOut));
        var sent = _http.Requests.Single();
        Assert.Equal("/v1/messages", sent.Path);
        Assert.Equal("test-key", sent.Headers["x-api-key"]);
        Assert.Equal("2026-10-21", sent.Headers["anthropic-version"]);
        var body = JsonNode.Parse(sent.Body!)!;
        Assert.Equal("be brief", body["system"]!.GetValue<string>());
        Assert.Equal("low", body["output_config"]!["effort"]!.GetValue<string>());
        var schema = body["output_config"]!["format"]!["schema"]!.ToJsonString();
        Assert.Contains("\"steps\"", schema);     // the property survives...
        Assert.DoesNotContain("minimum", schema); // ...its unsupported constraint doesn't
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("max_tokens")]
    public async Task Anthropic_refusal_or_truncation_is_a_failure(string stopReason)
    {
        _http.Respond = _ => Ok($$$"""{"content":[],"stop_reason":"{{{stopReason}}}","usage":{}}""");
        var client = new AnthropicClient(_http.Client("https://api/v1/"), "m", "k", null);
        await Assert.ThrowsAsync<LlmException>(() => client.CompleteJsonAsync(Request, default));
    }

    [Fact]
    public async Task Http_and_network_errors_become_LlmException()
    {
        var client = new OpenAiCompatClient(_http.Client("http://ollama/v1/"), "m", null);
        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };
        Assert.Contains("500", (await Assert.ThrowsAsync<LlmException>(() => client.CompleteJsonAsync(Request, default))).Message);

        _http.Respond = _ => throw new HttpRequestException("refused");
        await Assert.ThrowsAsync<LlmException>(() => client.CompleteJsonAsync(Request, default));
    }
}
