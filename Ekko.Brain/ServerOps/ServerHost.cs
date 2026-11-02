using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ekko.Brain.ServerOps;

public sealed class ServerOpsOptions
{
    /// <summary>The Docker proxy sidecar (ops/docker-proxy). It refuses everything the tools don't need.</summary>
    public string DockerUrl { get; init; } = "http://docker-proxy:2375/";

    /// <summary>Containers the agent may restart. The proxy's own rule must match (see haproxy.cfg).</summary>
    public List<string> Restartable { get; init; } = [];

    /// <summary>Folders the agent may measure, by name ("media" -> /host/media). Each is a read-only mount: the mounts are the allowlist.</summary>
    public Dictionary<string, string> Roots { get; init; } = [];

    public int MaxSteps { get; init; } = 8;
    public int RunTimeoutSeconds { get; init; } = 120;
    public int ToolTimeoutSeconds { get; init; } = 30;
    public int ApprovalHours { get; init; } = 12;
    public string RunsPath { get; init; } = "data/runs";
}

public sealed record ContainerInfo(string Name, string State, string Status, int RestartCount);

public sealed record DriveUsage(string Name, long TotalBytes, long FreeBytes);

public sealed record DockerUsage(long Images, long Containers, long Volumes, long BuildCache, long BuildCacheReclaimable);

public sealed record DirSize(string Path, long Bytes);

/// <summary>A failure already worded for the model (and the user).</summary>
public sealed class ServerHostException(string message) : Exception(message);

/// <summary>What the server-ops tools can see and do. Real: <see cref="DockerHost"/>. Scenario evals and tests: canned answers.</summary>
public interface IServerHost
{
    Task<IReadOnlyList<ContainerInfo>> ContainersAsync(CancellationToken ct);
    Task<string> LogsAsync(string container, int lines, CancellationToken ct);
    Task RestartAsync(string container, CancellationToken ct);
    Task<IReadOnlyList<DriveUsage>> DrivesAsync(CancellationToken ct);
    Task<DockerUsage> DockerUsageAsync(CancellationToken ct);
    Task<IReadOnlyList<DirSize>> LargestAsync(string root, string relative, int depth, CancellationToken ct);
}

/// <summary>Docker through the proxy, and the read-only mounted folders.</summary>
public sealed partial class DockerHost(HttpClient docker, ServerOpsOptions options) : IServerHost
{
    public const int MaxLogChars = 12_000;
    private const int MaxLineChars = 300;

    public async Task<IReadOnlyList<ContainerInfo>> ContainersAsync(CancellationToken ct)
    {
        using var list = await GetJsonAsync("containers/json?all=true", ct);
        var names = list.RootElement.EnumerateArray()
            .Select(c => (Name: c.GetProperty("Names")[0].GetString()!.TrimStart('/'), State: c.GetProperty("State").GetString() ?? "", Status: c.GetProperty("Status").GetString() ?? ""))
            .ToList();
        // The list has no restart count; a crash loop shows up only in each container's own record.
        var counts = await Task.WhenAll(names.Select(async n =>
        {
            using var inspect = await GetJsonAsync($"containers/{n.Name}/json", ct);
            return inspect.RootElement.TryGetProperty("RestartCount", out var r) ? r.GetInt32() : 0;
        }));
        return names.Select((n, i) => new ContainerInfo(n.Name, n.State, n.Status, counts[i])).OrderBy(c => c.Name).ToList();
    }

    public async Task<string> LogsAsync(string container, int lines, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"containers/{container}/logs?stdout=1&stderr=1&timestamps=1&tail={lines}", ct);
        return Tidy(Demux(await response.Content.ReadAsByteArrayAsync(ct)));
    }

    public async Task RestartAsync(string container, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Post, $"containers/{container}/restart?t=10", ct)).Dispose();

    /// <summary>Each mounted folder's whole drive: a bind mount of D:\Media reports D:'s size and free space.</summary>
    public Task<IReadOnlyList<DriveUsage>> DrivesAsync(CancellationToken ct)
    {
        var drives = new List<DriveUsage>();
        foreach (var (name, path) in options.Roots)
        {
            try
            {
                var drive = new DriveInfo(path);
                drives.Add(new DriveUsage(name, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new ServerHostException($"The {name} folder isn't mounted.");
            }
        }
        return Task.FromResult<IReadOnlyList<DriveUsage>>(drives);
    }

    public async Task<DockerUsage> DockerUsageAsync(CancellationToken ct)
    {
        using var df = await GetJsonAsync("system/df", ct);
        var root = df.RootElement;
        long Sum(string array, Func<JsonElement, long> size) =>
            root.TryGetProperty(array, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().Sum(size) : 0;
        static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) && n > 0 ? n : 0;
        static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        return new DockerUsage(
            Images: Long(root, "LayersSize"),
            Containers: Sum("Containers", c => Long(c, "SizeRw")),
            Volumes: Sum("Volumes", v => v.TryGetProperty("UsageData", out var u) ? Long(u, "Size") : 0),
            BuildCache: Sum("BuildCache", b => Long(b, "Size")),
            BuildCacheReclaimable: Sum("BuildCache", b => Flag(b, "InUse") || Flag(b, "Shared") ? 0 : Long(b, "Size")));
    }

    /// <summary>The folders <paramref name="depth"/> levels under root/relative, biggest first. The caller has checked the path.</summary>
    public Task<IReadOnlyList<DirSize>> LargestAsync(string root, string relative, int depth, CancellationToken ct) => Task.Run<IReadOnlyList<DirSize>>(() =>
    {
        var start = new DirectoryInfo(Path.Combine(options.Roots[root], relative));
        if (!start.Exists)
            throw new ServerHostException($"There's no folder {root}/{relative}.");
        var skipLinks = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var level = new List<DirectoryInfo> { start };
        for (var i = 0; i < depth; i++)
            level = level.SelectMany(d => d.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })).ToList();
        return level
            .Select(d => new DirSize(Path.GetRelativePath(options.Roots[root], d.FullName).Replace('\\', '/'),
                d.EnumerateFiles("*", skipLinks).Sum(f => { ct.ThrowIfCancellationRequested(); return f.Length; })))
            .OrderByDescending(d => d.Bytes)
            .ToList();
    }, ct);

    /// <summary>
    /// Docker sends a container's logs (when it has no terminal) as frames: 1 byte stream, 3 zero bytes, a 4-byte
    /// big-endian length, then the text. A container with a terminal sends plain text.
    /// </summary>
    public static string Demux(byte[] data)
    {
        if (data.Length < 8 || data[0] > 2 || data[1] != 0 || data[2] != 0 || data[3] != 0)
            return Encoding.UTF8.GetString(data);
        var text = new StringBuilder();
        for (var i = 0; i + 8 <= data.Length;)
        {
            var size = (data[i + 4] << 24) | (data[i + 5] << 16) | (data[i + 6] << 8) | data[i + 7];
            text.Append(Encoding.UTF8.GetString(data, i + 8, Math.Min(size, data.Length - i - 8)));
            i += 8 + size;
        }
        return text.ToString();
    }

    /// <summary>Colour codes out, each line cut to 300 characters, and the newest 12,000 characters kept: logs go to the model as data.</summary>
    public static string Tidy(string logs)
    {
        var lines = Ansi().Replace(logs, "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Select(l => l.Length > MaxLineChars ? l[..MaxLineChars] + "..." : l);
        var text = string.Join('\n', lines);
        return text.Length > MaxLogChars ? "...\n" + text[^MaxLogChars..] : text;
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, ct);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await docker.SendAsync(new HttpRequestMessage(method, path), ct);
        }
        catch (HttpRequestException)
        {
            throw new ServerHostException("The Docker proxy isn't answering.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ServerHostException("Docker took too long to answer.");
        }
        if (response.IsSuccessStatusCode)
            return response;
        var status = (int)response.StatusCode;
        response.Dispose();
        throw status switch
        {
            403 => new ServerHostException("The Docker proxy refused that."),
            404 => new ServerHostException("There's no container by that name."),
            _ => new ServerHostException($"Docker answered HTTP {status}."),
        };
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();
}
