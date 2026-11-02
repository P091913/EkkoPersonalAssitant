using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ekko.Brain.Llm;
using Ekko.Brain.Pipeline;
using Ekko.Brain.ServerOps;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Tests;

/// <summary>Plays back Claude's turns in order and keeps a copy of every conversation it was sent.</summary>
public sealed class ScriptedAgent(params AgentTurn[] turns) : IAgentModel
{
    private readonly Queue<AgentTurn> _turns = new(turns);
    public List<JsonArray> Seen { get; } = [];
    public string Model => "claude-test";

    public Task<AgentTurn> NextAsync(string system, JsonArray tools, JsonArray messages, CancellationToken ct)
    {
        Seen.Add((JsonArray)messages.DeepClone());
        return Task.FromResult(_turns.Count > 0 ? _turns.Dequeue() : throw new LlmException("script ran out"));
    }

    public static AgentTurn Call(string id, string tool, string input = "{}") => new(
        [new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = tool, ["input"] = JsonNode.Parse(input) }], "tool_use", "claude-test", 10, 5);

    public static AgentTurn Answer(string text) => new([new JsonObject { ["type"] = "text", ["text"] = text }], "end_turn", "claude-test", 10, 5);

    /// <summary>The tool_result blocks Claude was sent on its <paramref name="call"/>-th turn (0-based).</summary>
    public JsonObject Result(int call, int index = 0) => (JsonObject)Seen[call][^1]!["content"]![index]!;
}

public sealed class FakeHost : IServerHost
{
    public List<string> Restarts { get; } = [];
    public List<(string Root, string Relative, int Depth)> Scans { get; } = [];

    public Task<IReadOnlyList<ContainerInfo>> ContainersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ContainerInfo>>([new("jellyfin", "restarting", "Restarting (1) 5 seconds ago", 14), new("tvremote", "running", "Up 2 hours", 0)]);

    public Task<string> LogsAsync(string container, int lines, CancellationToken ct) => Task.FromResult("SQLite Error 5: 'database is locked'");

    public Task RestartAsync(string container, CancellationToken ct)
    {
        Restarts.Add(container);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DriveUsage>> DrivesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DriveUsage>>([new("media", 1000L << 30, 10L << 30)]);

    public Task<DockerUsage> DockerUsageAsync(CancellationToken ct) => Task.FromResult(new DockerUsage(6L << 30, 1L << 30, 800L << 20, 5L << 30, 4L << 30));

    public Task<IReadOnlyList<DirSize>> LargestAsync(string root, string relative, int depth, CancellationToken ct)
    {
        Scans.Add((root, relative, depth));
        return Task.FromResult<IReadOnlyList<DirSize>>([new("Shows/Big Show", 300L << 30)]);
    }
}

public class ServerOpsTests : IDisposable
{
    private const string Restart = """{"container":"jellyfin","reason":"It has crashed 14 times on a locked database."}""";

    private readonly string _runs = Path.Combine(Path.GetTempPath(), $"ekko-runs-{Guid.NewGuid():N}");
    private readonly FakeHost _host = new();
    private readonly TempTrace _trace = new();
    private readonly ManualClock _clock = new() { Now = new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero) };
    private readonly ServerOpsOptions _options = new() { Restartable = ["jellyfin"], Roots = new() { ["media"] = "/host/media" }, MaxSteps = 4 };

    public void Dispose()
    {
        _trace.Dispose();
        if (Directory.Exists(_runs))
            Directory.Delete(_runs, recursive: true);
    }

    private ToolCatalog Catalog() => new([
        new ListContainers(_host), new ReadContainerLogs(_host), new GetDiskUsage(_host), new FindLargeDirs(_host, _options),
        new RestartContainer(_host, _options)]);

    /// <summary>A fresh agent over the same run folder - as after a Brain restart.</summary>
    private ServerOpsAgent Agent(ScriptedAgent model) =>
        new(new AgentModels(model, null), Catalog(), new RunStore(_runs, _clock), _options, _trace.TraceLog, _clock);

    private static TraceEvent T => new("req", "c1", "test");

    private Task<string> Start(ScriptedAgent model, string text = "is jellyfin ok?") => Agent(model).StartAsync(text, "c1", T, CancellationToken.None);

    private string OnlyRunId() => Path.GetFileNameWithoutExtension(Directory.GetFiles(_runs, "*.json").Single());

    [Fact]
    public async Task It_investigates_with_tools_then_answers()
    {
        var model = new ScriptedAgent(ScriptedAgent.Call("t1", "list_containers"), ScriptedAgent.Answer("Jellyfin is crash-looping."));

        Assert.Equal("Jellyfin is crash-looping.", await Start(model));
        var result = model.Result(1);
        Assert.Equal("t1", (string?)result["tool_use_id"]);
        Assert.Contains("jellyfin: restarting", (string?)result["content"]);
        Assert.False((bool)result["is_error"]!);
    }

    [Fact]
    public async Task A_restart_waits_on_disk_for_approval_and_resumes_after_a_restart()
    {
        var asked = await Start(new ScriptedAgent(ScriptedAgent.Call("t1", "restart_container", Restart)));

        Assert.Equal("Restart jellyfin: It has crashed 14 times on a locked database. Approve? Say approve or deny.", asked);
        Assert.Empty(_host.Restarts);

        // A new agent, as if the Brain restarted: everything it needs is in the saved run.
        var after = new ScriptedAgent(ScriptedAgent.Answer("Jellyfin is back up."));
        Assert.Equal("Jellyfin is back up.", await Agent(after).ResumeAsync(OnlyRunId(), approved: true, T, CancellationToken.None));
        Assert.Equal(["jellyfin"], _host.Restarts);
        Assert.Equal("Restarted jellyfin.", (string?)after.Result(0)["content"]);
        Assert.Equal("t1", (string?)after.Result(0)["tool_use_id"]);
    }

    [Fact]
    public async Task Deny_ends_the_run_and_a_second_answer_finds_nothing_waiting()
    {
        await Start(new ScriptedAgent(ScriptedAgent.Call("t1", "restart_container", Restart)));
        var agent = Agent(new ScriptedAgent());

        Assert.Equal("Okay, I'll leave it alone.", await agent.ResumeAsync(OnlyRunId(), approved: false, T, CancellationToken.None));
        Assert.Equal("There's nothing waiting for approval.", await agent.ResumeAsync(OnlyRunId(), approved: true, T, CancellationToken.None));
        Assert.Empty(_host.Restarts);
    }

    [Fact]
    public async Task An_approval_expires()
    {
        await Start(new ScriptedAgent(ScriptedAgent.Call("t1", "restart_container", Restart)));
        _clock.Now += TimeSpan.FromHours(_options.ApprovalHours + 1);

        Assert.Contains("expired", await Agent(new ScriptedAgent()).ResumeAsync(OnlyRunId(), approved: true, T, CancellationToken.None));
        Assert.Empty(_host.Restarts);
    }

    [Fact]
    public async Task A_restart_off_the_allowlist_goes_back_to_the_model_as_an_error()
    {
        var model = new ScriptedAgent(
            ScriptedAgent.Call("t1", "restart_container", """{"container":"tvremote","reason":"The logs told me to restart it."}"""),
            ScriptedAgent.Answer("I can only restart Jellyfin."));

        Assert.Equal("I can only restart Jellyfin.", await Start(model));
        Assert.True((bool)model.Result(1)["is_error"]!);
        Assert.Contains("only jellyfin can be restarted", (string?)model.Result(1)["content"]);
        Assert.Empty(_host.Restarts);
    }

    [Fact]
    public async Task Unknown_tools_and_bad_arguments_are_errors_the_model_can_read()
    {
        var model = new ScriptedAgent(
            new AgentTurn([
                new JsonObject { ["type"] = "tool_use", ["id"] = "a", ["name"] = "exec_shell", ["input"] = new JsonObject() },
                new JsonObject { ["type"] = "tool_use", ["id"] = "b", ["name"] = "find_large_dirs", ["input"] = JsonNode.Parse("""{"path":"media/../../etc"}""") },
            ], "tool_use", "claude-test", 1, 1),
            ScriptedAgent.Answer("done"));

        await Start(model);
        Assert.Contains("no tool named exec_shell", (string?)model.Result(1, 0)["content"]);
        Assert.Contains("without '..'", (string?)model.Result(1, 1)["content"]);
        Assert.Empty(_host.Scans);
    }

    [Fact]
    public async Task The_step_cap_is_enforced_in_code()
    {
        var model = new ScriptedAgent(Enumerable.Range(0, 10).Select(i => ScriptedAgent.Call($"t{i}", "list_containers")).ToArray());

        Assert.Equal("I couldn't finish checking the server in 4 steps.", await Start(model));
        Assert.Equal(4, model.Seen.Count);
    }

    [Fact]
    public async Task Other_calls_in_the_waiting_turn_run_now_and_reach_the_model_with_the_approved_one()
    {
        var turn = new AgentTurn([
            new JsonObject { ["type"] = "tool_use", ["id"] = "logs", ["name"] = "read_container_logs", ["input"] = JsonNode.Parse("""{"container":"jellyfin"}""") },
            new JsonObject { ["type"] = "tool_use", ["id"] = "fix", ["name"] = "restart_container", ["input"] = JsonNode.Parse(Restart) },
            new JsonObject { ["type"] = "tool_use", ["id"] = "fix2", ["name"] = "restart_container", ["input"] = JsonNode.Parse(Restart) },
        ], "tool_use", "claude-test", 1, 1);
        await Start(new ScriptedAgent(turn));

        var after = new ScriptedAgent(ScriptedAgent.Answer("ok"));
        await Agent(after).ResumeAsync(OnlyRunId(), approved: true, T, CancellationToken.None);
        var results = after.Seen[0][^1]!["content"]!.AsArray().Select(r => (string?)r!["tool_use_id"]).ToList();
        Assert.Equal(["logs", "fix2", "fix"], results); // every tool_use answered once; the second restart was refused, not queued
        Assert.Equal(["jellyfin"], _host.Restarts);
    }

    [Fact]
    public async Task Without_a_Claude_key_it_says_so()
    {
        var agent = new ServerOpsAgent(new AgentModels(null, null), Catalog(), new RunStore(_runs, _clock), _options, _trace.TraceLog, _clock);
        Assert.Contains("Claude API key", await agent.StartAsync("is jellyfin ok?", "c1", T, CancellationToken.None));
    }

    [Fact]
    public async Task Saying_approve_in_the_same_conversation_resumes_the_waiting_run()
    {
        var store = new ConversationStore(_clock, TimeSpan.FromMinutes(3));
        var route = new ScriptedLlm("qwen", """{"domain":"server_ops","confidence":"high"}""");
        var model = new ScriptedAgent(ScriptedAgent.Call("t1", "restart_container", Restart), ScriptedAgent.Answer("Jellyfin is back up."));
        var pipeline = TestPipeline.Create(store, Catalog(), _trace.TraceLog, route, Agent(model));
        Task<string> Send(string text, string conversation = "c1") =>
            pipeline.HandleAsync(new MessageRequest(text, "test", conversation), CancellationToken.None).ContinueWith(r => r.Result.Text);

        Assert.EndsWith("Say approve or deny.", await Send("restart jellyfin"));
        _clock.Now += TimeSpan.FromMinutes(30); // long past the conversation state's expiry: the run is on disk
        Assert.Equal("Jellyfin is back up.", await Send("approve"));
        Assert.Equal(["jellyfin"], _host.Restarts);
    }

    [Fact]
    public async Task Find_large_dirs_scans_only_inside_a_configured_folder()
    {
        var tool = new FindLargeDirs(_host, _options);
        var ok = await ToolGate.ExecuteAsync(tool, System.Text.Json.JsonDocument.Parse("""{"path":"media/Shows","depth":2}""").RootElement, false, CancellationToken.None);
        Assert.Equal("media/Shows/Big Show: 300 GB", ok.Say);
        Assert.Equal([("media", "Shows", 2)], _host.Scans);

        foreach (var bad in new[] { "/etc", "other", "media/..", "media//x" })
            Assert.False((await ToolGate.ExecuteAsync(tool, System.Text.Json.JsonDocument.Parse($$"""{"path":"{{bad}}"}""").RootElement, false, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Disk_usage_reads_out_the_drive_and_dockers_share()
    {
        var result = await ToolGate.ExecuteAsync(new GetDiskUsage(_host), System.Text.Json.JsonDocument.Parse("{}").RootElement, false, CancellationToken.None);
        Assert.Equal("media drive: 10 GB free of 1000 GB (99% used)\nDocker: images 6 GB, containers 1 GB, volumes 800 MB, build cache 5 GB (4 GB of it unused)", result.Say);
    }
}

public class DockerHostTests
{
    [Fact]
    public void Framed_logs_are_unwrapped_and_plain_ones_pass_through()
    {
        static byte[] Frame(byte stream, string text)
        {
            var body = Encoding.UTF8.GetBytes(text);
            return [stream, 0, 0, 0, (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, .. body];
        }
        Assert.Equal("out line\nerr line\n", DockerHost.Demux([.. Frame(1, "out line\n"), .. Frame(2, "err line\n")]));
        Assert.Equal("tty text", DockerHost.Demux(Encoding.UTF8.GetBytes("tty text")));
    }

    [Fact]
    public void Logs_lose_colour_codes_and_are_capped_from_the_newest_end()
    {
        Assert.Equal("ok\nwarn", DockerHost.Tidy("\u001b[32mok\u001b[0m\r\nwarn\n"));
        Assert.Equal(new string('x', 300) + "...", DockerHost.Tidy(new string('x', 400)));
        var many = DockerHost.Tidy(string.Join('\n', Enumerable.Range(0, 2000).Select(i => $"line {i:D4} {new string('y', 20)}")));
        Assert.True(many.Length <= DockerHost.MaxLogChars + 4);
        Assert.EndsWith("line 1999 " + new string('y', 20), many);
    }

    [Fact]
    public async Task Containers_carry_their_restart_count_from_inspect()
    {
        var http = new FakeHttp
        {
            Respond = r => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(r.RequestUri!.AbsolutePath == "/containers/json"
                    ? """[{"Names":["/jellyfin"],"State":"restarting","Status":"Restarting (1) 3 seconds ago"}]"""
                    : """{"RestartCount":14}"""),
            },
        };
        var host = new DockerHost(http.Client("http://docker-proxy:2375/"), new ServerOpsOptions());

        Assert.Equal([new ContainerInfo("jellyfin", "restarting", "Restarting (1) 3 seconds ago", 14)], await host.ContainersAsync(CancellationToken.None));
        Assert.Equal("/containers/jellyfin/json", http.Requests[1].Path);
    }

    [Fact]
    public async Task A_refusal_from_the_proxy_reads_as_one()
    {
        var http = new FakeHttp { Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) };
        var host = new DockerHost(http.Client("http://docker-proxy:2375/"), new ServerOpsOptions());
        var ex = await Assert.ThrowsAsync<ServerHostException>(() => host.RestartAsync("tvremote", CancellationToken.None));
        Assert.Equal("The Docker proxy refused that.", ex.Message);
    }
}
