using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ekko.Brain;
using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Runs every utterance through the fast path, router and extractor exactly as the Brain wires them (AddEkko),
// and scores each step. It never runs a tool: nothing on the PC or TV changes.
//   dotnet run --project Ekko.Evals            (uses Ekko.Brain/appsettings.json + its user-secrets)
//   dotnet run --project Ekko.Evals -- server-ops   (agent scenarios against canned servers; real Claude, a few cents)

var here = Here();
var brainDir = Path.GetFullPath(Path.Combine(here, "..", "Ekko.Brain"));
var config = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(brainDir, "appsettings.json"))
    .AddUserSecrets(typeof(ApiKey).Assembly, optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

if (args.Contains("server-ops"))
{
    await ServerOpsScenarios.RunAsync(config, brainDir);
    return;
}

var tracePath = Path.Combine(Path.GetTempPath(), $"ekko-eval-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
var services = new ServiceCollection().AddLogging().AddEkko(config, brainDir);
services.AddSingleton(new TraceLog(tracePath, TimeProvider.System)); // last registration wins: keeps eval runs out of the real trace
using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var catalog = scope.ServiceProvider.GetRequiredService<ToolCatalog>();
var router = scope.ServiceProvider.GetRequiredService<Router>();
var handler = scope.ServiceProvider.GetRequiredService<DomainHandler>();

var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var cases = File.ReadLines(Path.Combine(here, "utterances.jsonl"))
    .Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("//"))
    .Select(l => JsonSerializer.Deserialize<Case>(l, web)!)
    .ToList();

string Model(string step) => $"{config[$"Llm:Clients:{config[$"Llm:Steps:{step}:Primary"]}:Model"]}"
    + (config[$"Llm:Steps:{step}:Fallback"] is { } f ? $" -> {config[$"Llm:Clients:{f}:Model"]}" : "");
Console.WriteLine($"Ekko evals: {cases.Count} utterances | route: {Model("route")} | extract: {Model("extract")}");

var outcomes = new List<Outcome>();
for (var i = 0; i < cases.Count; i++)
{
    var c = cases[i];
    var t = new TraceEvent($"eval-{i}", "eval", "eval");
    var o = new Outcome(c);
    if (FastPath.Match(c.Text) is { } fast && catalog.Find(fast.Tool) is { } fastTool)
    {
        (o.Fast, o.Domain, o.Tool, o.Args) = (true, Router.Name(fastTool.Domain), fast.Tool, fast.Args);
    }
    else
    {
        var clock = Stopwatch.StartNew();
        var route = await router.RouteAsync(c.Text, t, default);
        (o.RouteMs, o.Domain, o.Low) = (clock.ElapsedMilliseconds, route is null ? null : Router.Name(route.Domain), route?.Confidence == Confidence.Low);
        if (route is not null && o.Domain == c.Domain && c.Tool is not null)
        {
            clock.Restart();
            var call = await handler.ExtractAsync(route.Domain, c.Text, t, default);
            (o.ExtractMs, o.Extracted, o.Tool, o.Args) = (clock.ElapsedMilliseconds, true, call?.Tool, call?.Args);
        }
    }
    o.Score(catalog);
    outcomes.Add(o);
    Console.Write(o.Correct ? "." : "x");
}
Console.WriteLine();

// Retries and escalations come from the trace lines StructuredCall wrote for each case.
var modelLines = File.ReadLines(tracePath).Select(l => JsonDocument.Parse(l).RootElement)
    .Where(l => l.GetProperty("step").GetString()!.StartsWith("llm:")).ToList();
string Attempts(string step)
{
    var byCase = modelLines.Where(l => l.GetProperty("step").GetString() == step)
        .GroupBy(l => l.GetProperty("request_id").GetString()).ToList();
    int With(int attempt) => byCase.Count(g => g.Any(l => l.GetProperty("attempt").GetInt32() == attempt));
    var firstOk = byCase.Count(g => g.Any(l => l.GetProperty("attempt").GetInt32() == 1 && l.GetProperty("ok").GetBoolean()));
    return $"first try valid {firstOk}/{byCase.Count}, retried {With(2)}, escalated {With(3)}";
}

static string Pct(int n, int of) => of == 0 ? "-" : $"{100.0 * n / of:0.#}%";
static string Latency(IEnumerable<long> ms)
{
    var sorted = ms.Order().ToList();
    return sorted.Count == 0 ? "" : $"p50 {sorted[sorted.Count / 2] / 1000.0:0.0}s, p95 {sorted[(int)Math.Min(sorted.Count - 1, sorted.Count * 0.95)] / 1000.0:0.0}s";
}

var fastHits = outcomes.Where(o => o.Fast).ToList();
var routed = outcomes.Where(o => !o.Fast).ToList();
var extracted = outcomes.Where(o => o.Extracted).ToList();
var correct = outcomes.Count(o => o.Correct);
Console.WriteLine($"""

    Fast path    {fastHits.Count} hits, {fastHits.Count(o => o.Correct)} correct
    Routing      {routed.Count(o => o.DomainOk)}/{routed.Count} correct ({Pct(routed.Count(o => o.DomainOk), routed.Count)}) | low confidence {routed.Count(o => o.Low)} | {Latency(routed.Select(o => o.RouteMs))} | {Attempts("llm:route")}
    Extraction   tool {extracted.Count(o => o.ToolOk)}/{extracted.Count}, args {extracted.Count(o => o.ArgsOk)}/{extracted.Count} ({Pct(extracted.Count(o => o.ToolOk && o.ArgsOk), extracted.Count)}) | {Latency(extracted.Select(o => o.ExtractMs))} | {Attempts("llm:extract")}
    End to end   {correct}/{outcomes.Count} ({Pct(correct, outcomes.Count)})
    """);

foreach (var o in outcomes.Where(o => !o.Correct))
    Console.WriteLine(!o.DomainOk
        ? $"  [{(o.Fast ? "fast" : "route")}]   \"{o.Case.Text}\" -> {o.Domain ?? "(no answer)"}, expected {o.Case.Domain}"
        : $"  [extract] \"{o.Case.Text}\" -> {o.Tool ?? "(no answer)"} {o.Args?.GetRawText()}, expected {o.Case.Tool} {o.Case.Args?.GetRawText()}");
Console.WriteLine($"\nTrace: {tracePath}");

// One line per run, committed with the code, so accuracy can be tracked across prompt and model changes.
File.AppendAllText(Path.Combine(here, "history.jsonl"), JsonSerializer.Serialize(new
{
    ts = DateTimeOffset.Now,
    route_model = Model("route"),
    extract_model = Model("extract"),
    cases = outcomes.Count,
    end_to_end = correct,
    routing = $"{routed.Count(o => o.DomainOk)}/{routed.Count}",
    extraction = $"{extracted.Count(o => o.ToolOk && o.ArgsOk)}/{extracted.Count}",
    route_p50_ms = routed.Select(o => o.RouteMs).Order().ElementAtOrDefault(routed.Count / 2),
    extract_p50_ms = extracted.Select(o => o.ExtractMs).Order().ElementAtOrDefault(extracted.Count / 2),
}) + "\n");

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

/// <param name="Tool">Expected tool; "none" = no tool fits; null = routing-only case (domain not built).</param>
sealed record Case(string Text, string Domain, string? Tool = null, JsonElement? Args = null);

sealed class Outcome(Case c)
{
    public Case Case { get; } = c;
    public bool Fast, Low, Extracted;
    public string? Domain, Tool;
    public JsonElement? Args;
    public long RouteMs, ExtractMs;
    public bool DomainOk, ToolOk, ArgsOk;
    public bool Correct => DomainOk && ToolOk && ArgsOk;

    public void Score(ToolCatalog catalog)
    {
        DomainOk = Domain == Case.Domain;
        ToolOk = Case.Tool is null || Tool == Case.Tool;
        ArgsOk = Case.Tool is null or ToolCall.None
                 || ToolOk && catalog.Find(Case.Tool) is { } tool
                 && Acceptable(Case.Args).Any(expected => Same(Normalize(expected, tool.Schema), Normalize(Args, tool.Schema)));
    }

    /// <summary>"args" is one object, or a list of answers that behave identically.</summary>
    private static IEnumerable<JsonElement?> Acceptable(JsonElement? args) =>
        args is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().Select(e => (JsonElement?)e) : [args];

    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Nulls dropped, schema defaults filled in, strings compared the way the fast path compares text.</summary>
    private static Dictionary<string, string> Normalize(JsonElement? args, JsonNode schema)
    {
        var result = new Dictionary<string, string>();
        if (args is { ValueKind: JsonValueKind.Object } obj)
            foreach (var p in obj.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null))
                result[p.Name] = Value(p.Value);
        if (schema["properties"] is JsonObject properties)
            foreach (var (name, property) in properties)
                if (!result.ContainsKey(name) && property?["default"] is { } fallback)
                    result[name] = Value(JsonSerializer.SerializeToElement(fallback));
        return result;
    }

    // Numbers by value: 220, 220.0 and 220.00 are the same answer.
    private static string Value(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => Utterance.Simplify(v.GetString()!),
        JsonValueKind.Number => v.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => v.GetRawText(),
    };
}
