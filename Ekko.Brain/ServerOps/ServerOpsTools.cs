using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Ekko.Brain.Tools;

namespace Ekko.Brain.ServerOps;

public sealed record ContainerArgs(
    [property: RegularExpression("^[a-zA-Z0-9_.-]{1,64}$"), Description("The container's name, from list_containers.")] string Container,
    [property: Range(1, 200), Description("How many of the newest lines to read.")] int Lines = 50);

public sealed record LargeDirsArgs(
    [property: MinLength(1), MaxLength(200), Description("A folder to look inside: 'media', or one under it like 'media/Shows'.")] string Path,
    [property: Range(1, 3), Description("How many folder levels down to compare.")] int Depth = 1);

public sealed record RestartArgs(
    [property: RegularExpression("^[a-zA-Z0-9_.-]{1,64}$"), Description("The container to restart.")] string Container,
    [property: MinLength(5), MaxLength(300), Description("One sentence the user reads before approving: what you found that a restart would fix.")] string Reason);

[RiskClass(Risk.ReadOnly)]
public sealed class ListContainers(IServerHost host)
    : Tool<NoArgs>("list_containers", Domain.ServerOps, "Every container on the server: running or not, health, uptime, and how often it has restarted.")
{
    protected override Task<ToolResult> RunAsync(NoArgs args, CancellationToken ct) => ServerOpsResult.Try(async () =>
        string.Join('\n', (await host.ContainersAsync(ct)).Select(c => $"{c.Name}: {c.State}, {c.Status}, restarted {c.RestartCount} times")));
}

[RiskClass(Risk.ReadOnly)]
public sealed class ReadContainerLogs(IServerHost host)
    : Tool<ContainerArgs>("read_container_logs", Domain.ServerOps, "The newest lines of one container's log, with timestamps.")
{
    protected override Task<ToolResult> RunAsync(ContainerArgs args, CancellationToken ct) => ServerOpsResult.Try(async () =>
        await host.LogsAsync(args.Container, args.Lines, ct) is { Length: > 0 } logs ? logs : $"{args.Container} has no log lines.");
}

[RiskClass(Risk.ReadOnly)]
public sealed class GetDiskUsage(IServerHost host)
    : Tool<NoArgs>("get_disk_usage", Domain.ServerOps, "Free space on the media drive, and how much Docker uses for images, containers, volumes and build cache.")
{
    protected override Task<ToolResult> RunAsync(NoArgs args, CancellationToken ct) => ServerOpsResult.Try(async () =>
    {
        var drives = (await host.DrivesAsync(ct)).Select(d =>
            $"{d.Name} drive: {Size.Of(d.FreeBytes)} free of {Size.Of(d.TotalBytes)} ({100 - 100 * d.FreeBytes / Math.Max(d.TotalBytes, 1)}% used)");
        var docker = await host.DockerUsageAsync(ct);
        return string.Join('\n', drives.Append(
            $"Docker: images {Size.Of(docker.Images)}, containers {Size.Of(docker.Containers)}, volumes {Size.Of(docker.Volumes)}, " +
            $"build cache {Size.Of(docker.BuildCache)} ({Size.Of(docker.BuildCacheReclaimable)} of it unused)"));
    });
}

[RiskClass(Risk.ReadOnly)]
public sealed class FindLargeDirs(IServerHost host, ServerOpsOptions options)
    : Tool<LargeDirsArgs>("find_large_dirs", Domain.ServerOps,
        $"The biggest folders under a folder, to see what fills a drive. Folders it can look in: {string.Join(", ", options.Roots.Keys)}.")
{
    protected override string? Check(LargeDirsArgs args) => Split(args.Path) is null
        ? $"path must be one of {string.Join(", ", options.Roots.Keys)}, or a folder under one, without '..'"
        : null;

    protected override Task<ToolResult> RunAsync(LargeDirsArgs args, CancellationToken ct) => ServerOpsResult.Try(async () =>
    {
        var (root, relative) = Split(args.Path)!.Value;
        var dirs = await host.LargestAsync(root, relative, args.Depth, ct);
        return dirs.Count == 0
            ? $"{args.Path} has no folders {args.Depth} level(s) down."
            : string.Join('\n', dirs.Take(10).Select(d => $"{root}/{d.Path}: {Size.Of(d.Bytes)}")) + (dirs.Count > 10 ? $"\n...and {dirs.Count - 10} smaller" : "");
    });

    /// <summary>"media/Shows" -> (media, Shows). Null unless the first part is a configured root and no part climbs out.</summary>
    private (string Root, string Relative)? Split(string path)
    {
        var parts = path.Replace('\\', '/').Trim('/').Split('/');
        if (!options.Roots.ContainsKey(parts[0]) || parts.Any(p => p is "" or "." or ".."))
            return null;
        return (parts[0], string.Join('/', parts[1..]));
    }
}

/// <summary>Approve class: the gate never runs it until the user approves this exact call, and the agent saves the run while it waits.</summary>
[RiskClass(Risk.Approve)]
public sealed class RestartContainer(IServerHost host, ServerOpsOptions options)
    : Tool<RestartArgs>("restart_container", Domain.ServerOps,
        $"Restart a container that is crashed, stuck or misbehaving. The user must approve first. Only these can be restarted: {string.Join(", ", options.Restartable)}.")
{
    protected override string? Check(RestartArgs args) =>
        options.Restartable.Contains(args.Container) ? null : $"only {string.Join(", ", options.Restartable)} can be restarted";

    protected override string Describe(RestartArgs args) => $"Restart {args.Container}: {args.Reason.TrimEnd('.')}";

    protected override Task<ToolResult> RunAsync(RestartArgs args, CancellationToken ct) => ServerOpsResult.Try(async () =>
    {
        await host.RestartAsync(args.Container, ct);
        return $"Restarted {args.Container}.";
    });
}

internal static class ServerOpsResult
{
    /// <summary>Failures come back as text too: the agent reads them and decides what to do next.</summary>
    public static async Task<ToolResult> Try(Func<Task<string>> work)
    {
        try
        {
            return ToolResult.Done(await work());
        }
        catch (ServerHostException ex)
        {
            return ToolResult.Failed(ex.Message);
        }
    }
}

public static class Size
{
    public static string Of(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{(bytes / (double)(1L << 40)).ToString("0.#", CultureInfo.InvariantCulture)} TB",
        >= 1L << 30 => $"{(bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture)} GB",
        >= 1L << 20 => $"{bytes >> 20} MB",
        _ => $"{bytes >> 10} KB",
    };
}
