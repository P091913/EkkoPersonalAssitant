using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ekko.Files;

/// <summary>A file someone sent Ekko. Its bytes sit in the same folder under its id.</summary>
public sealed record StoredFile(string Id, string Name, string Type, long Size, string From, DateTimeOffset At);

/// <summary>Every answer has a sentence the channel shows as is; File is set when it was kept.</summary>
public sealed record Saved(bool Ok, string Message, StoredFile? File = null);

/// <summary>
/// Photos and PDFs sent to Ekko, for the desk to show (DR-40). Each file is its bytes plus a small JSON beside it, so
/// two uploads at once never share a write and there's no index to corrupt. Photos and PDFs saved in a folder on the PC
/// (Downloads) are listed and served alongside them, read straight from that folder and never copied (DR-41).
/// </summary>
public sealed class FileStore(string folder, TimeProvider clock, string? pcFolder = null)
{
    /// <summary>Telegram's limit on what a bot can download, so every channel gets the same one.</summary>
    public const int MaxBytes = 20 * 1024 * 1024;
    /// <summary>"From" for a file in the PC folder. No client key may be named this.</summary>
    public const string Pc = "pc";
    private const int MaxName = 100;
    private const string Pdf = "application/pdf";

    private static readonly Dictionary<string, string> Extensions = new()
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif", [".webp"] = "image/webp", [".pdf"] = Pdf,
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static ReadOnlySpan<byte> Jpeg => [0xFF, 0xD8, 0xFF];
    private static ReadOnlySpan<byte> Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public Saved Save(string? name, string from, byte[] bytes)
    {
        if (bytes.Length == 0)
            return new(false, "That file is empty.");
        if (bytes.Length > MaxBytes)
            return new(false, "That file is over 20 MB, too big to keep.");
        if (Sniff(bytes) is not { } type)
            return new(false, "I can only keep photos and PDFs, and that file is neither.");

        Directory.CreateDirectory(folder);
        var file = new StoredFile(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), Clean(name, type), type,
            bytes.Length, from, clock.GetUtcNow());
        Write(Path.Combine(folder, file.Id), bytes);
        // The info goes last, so a file is only listed once all its bytes are there.
        Write(Path.Combine(folder, file.Id + ".json"), JsonSerializer.SerializeToUtf8Bytes(file, Json));
        return new(true, $"Saved “{file.Name}”.", file);
    }

    /// <summary>
    /// Newest first. With words ("the invoice", "receipt photo"), only files whose name has each word, where a kind word
    /// ("photo", "pdf") means that kind instead. Words like "it" or "last" pick nothing, so "it" is simply the newest.
    /// </summary>
    // shortcut: reads every file's info on each call; fine for a few thousand files, add an index past that.
    public IReadOnlyList<StoredFile> Recent(int count, string? words = null)
    {
        var wanted = Regex.Split((words ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Select(w => w.Length > 3 && w.EndsWith('s') ? w[..^1] : w) // "invoices" finds invoice.pdf
            .Where(w => w.Length > 0 && !Filler.Contains(w)).ToList();
        return Kept().Concat(OnPc())
            .Where(f => wanted.All(w => Kinds.TryGetValue(w, out var kind) ? f.Type.StartsWith(kind)
                                                                            : f.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.At)
            .Select(f => f.From == Pc ? Checked(f) : f) // a PC file's bytes are read only once it could be returned
            .OfType<StoredFile>().Take(count).ToList();
    }

    private static readonly HashSet<string> Filler =
    [
        "the", "a", "an", "my", "it", "this", "that", "one", "file", "last", "latest", "newest", "new", "i", "just", "sent", "me", "you",
        "from", "download", "downloaded", "folder",
    ];

    private static readonly Dictionary<string, string> Kinds = new()
    {
        ["photo"] = "image/", ["picture"] = "image/", ["pic"] = "image/", ["image"] = "image/", ["pdf"] = Pdf, ["document"] = Pdf,
    };

    /// <summary>Null for an id this store never handed out, so a request can't name a path ("../x").</summary>
    public StoredFile? Find(string id)
    {
        if (id.Length != 16 || !id.All(char.IsAsciiHexDigitLower))
            return null;
        var info = Path.Combine(folder, id + ".json");
        return File.Exists(info) ? Read(info) : OnPc().FirstOrDefault(f => f.Id == id) is { } onPc ? Checked(onPc) : null;
    }

    /// <summary>A PC file's name comes from listing its folder, never from a request.</summary>
    public string PathOf(StoredFile file) => file.From == Pc ? Path.Combine(pcFolder!, file.Name) : Path.Combine(folder, file.Id);

    private IEnumerable<StoredFile> Kept() =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.json").Select(Read).OfType<StoredFile>() : [];

    /// <summary>Photos and PDFs straight in the PC folder: no subfolders, no hidden files, nothing half-downloaded.</summary>
    private IEnumerable<StoredFile> OnPc() => pcFolder is null || !Directory.Exists(pcFolder) ? []
        : new DirectoryInfo(pcFolder).EnumerateFiles("*", new EnumerationOptions())
            .Where(f => f.Length is > 0 and <= MaxBytes && Extensions.ContainsKey(f.Extension.ToLowerInvariant()))
            .Select(f => new StoredFile(PcId(f), f.Name, Extensions[f.Extension.ToLowerInvariant()], f.Length, Pc,
                new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)));

    /// <summary>The same id while the file is unchanged; a new download with the same name gets a new one.</summary>
    private static string PcId(FileInfo f) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}")))[..16];

    /// <summary>The PC file with the type its bytes say, or null when they aren't a photo or PDF, or it's gone.</summary>
    private StoredFile? Checked(StoredFile file)
    {
        try
        {
            using var stream = File.OpenRead(PathOf(file));
            var head = new byte[1024];
            var type = Sniff(head.AsSpan(0, stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)));
            return type is null ? null : file with { Type = type };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What the bytes are, whatever the name says. Only what a browser shows safely: no SVG, which can hold script.</summary>
    public static string? Sniff(ReadOnlySpan<byte> b) =>
        b.StartsWith(Jpeg) ? "image/jpeg"
        : b.StartsWith(Png) ? "image/png"
        : b.StartsWith("GIF87a"u8) || b.StartsWith("GIF89a"u8) ? "image/gif"
        : b.Length >= 12 && b.StartsWith("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        // PDF readers accept the header anywhere in the first 1 KB.
        : b[..Math.Min(b.Length, 1024)].IndexOf("%PDF-"u8) >= 0 ? Pdf
        : null;

    /// <summary>Just the file's own name, no folders or control characters, and short enough for a card on the desk.</summary>
    private static string Clean(string? name, string type)
    {
        name ??= "";
        name = new string(name[(name.LastIndexOfAny(['/', '\\']) + 1)..].Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > MaxName)
        {
            var ext = Path.GetExtension(name) is { Length: <= 10 } e ? e : ""; // keep ".pdf", not a 90-character "extension"
            name = name[..(MaxName - ext.Length)].TrimEnd() + ext;
        }
        return name.Length > 0 ? name : type == Pdf ? "document.pdf" : "photo";
    }

    private static StoredFile? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<StoredFile>(File.ReadAllBytes(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static void Write(string path, byte[] bytes)
    {
        File.WriteAllBytes(path + ".tmp", bytes);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
