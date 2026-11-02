using System.Text.Json;
using Ekko.Brain.Pipeline;

namespace Ekko.Brain.Tests;

public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A TraceLog writing to a temp file, with the lines readable back as JSON.</summary>
public sealed class TempTrace : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ekko-trace-{Guid.NewGuid():N}.jsonl");
    public TraceLog TraceLog { get; }

    public TempTrace() => TraceLog = new TraceLog(Path, TimeProvider.System);

    public List<JsonElement> Lines() =>
        File.Exists(Path) ? File.ReadAllLines(Path).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList() : [];

    public void Dispose() => File.Delete(Path);
}

/// <summary>Records every request and answers with <see cref="Respond"/>. Bodies are captured eagerly, before the content is disposed.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string? Body, Dictionary<string, string> Headers)> Requests { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
        _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") };

    public HttpClient Client(string baseUrl = "http://fake/") => new(this) { BaseAddress = new Uri(baseUrl) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body, headers));
        return Respond(request);
    }
}
