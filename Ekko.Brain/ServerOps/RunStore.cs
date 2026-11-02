using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ekko.Brain.ServerOps;

public static class RunStatus
{
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Done = "done";
    public const string Denied = "denied";
    public const string Failed = "failed";
    public const string Expired = "expired";
}

/// <summary>
/// One server-ops run: the whole Claude conversation and where it stands. Saved after every step, so a run waiting
/// for approval survives a restart and resumes exactly where it stopped.
/// </summary>
public sealed class SavedRun
{
    public required string Id { get; init; }
    public required string ConversationId { get; init; }
    public required string Request { get; init; }
    public required DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; set; }
    public string Status { get; set; } = RunStatus.Running;

    /// <summary>The model that took the first step. A run never switches: replayed thinking only verifies on the model that wrote it.</summary>
    public string? Model { get; set; }

    public int Steps { get; set; }
    public JsonArray Messages { get; init; } = [];

    /// <summary>The Approve-class tool_use block held by the gate.</summary>
    public JsonObject? Waiting { get; set; }

    /// <summary>Results of the other calls in the same turn: they go back to the model together with the approved one's.</summary>
    public JsonArray WaitingResults { get; set; } = [];

    public string? Question { get; set; }
    public string? Reply { get; set; }
}

public sealed partial class RunStore(string directory, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Held while a waiting run is resumed, so two approvals can't both run the call.</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public void Save(SavedRun run)
    {
        Directory.CreateDirectory(directory);
        run.Updated = clock.GetUtcNow();
        var path = PathFor(run.Id)!;
        // Write then rename: a crash mid-write leaves the old file, never half of one.
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(run, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>Null for an unknown id, or one that isn't an id at all (it arrives in a URL).</summary>
    public SavedRun? Load(string id) =>
        PathFor(id) is { } path && File.Exists(path) ? JsonSerializer.Deserialize<SavedRun>(File.ReadAllText(path), Json) : null;

    /// <summary>Runs waiting for approval, newest first. Only files touched within the window are read.</summary>
    public List<SavedRun> Waiting(TimeSpan window)
    {
        if (!Directory.Exists(directory))
            return [];
        var since = clock.GetUtcNow() - window;
        return new DirectoryInfo(directory).EnumerateFiles("*.json")
            .Where(f => f.LastWriteTimeUtc >= since.UtcDateTime)
            .Select(f => Load(Path.GetFileNameWithoutExtension(f.Name)))
            .OfType<SavedRun>()
            .Where(r => r.Status == RunStatus.Waiting && r.Updated >= since)
            .OrderByDescending(r => r.Updated)
            .ToList();
    }

    private string? PathFor(string id) => IdPattern().IsMatch(id) ? Path.Combine(directory, id + ".json") : null;

    [GeneratedRegex("^[a-f0-9]{12}$")]
    private static partial Regex IdPattern();
}
