using System.Text;

namespace Ekko.Files.Tests;

public sealed class Clock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class FileStoreTests : IDisposable
{
    private static readonly byte[] Photo = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ekko-files-" + Guid.NewGuid().ToString("N"));
    private readonly string _downloads = Path.Combine(Path.GetTempPath(), "ekko-downloads-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly FileStore _store;

    public FileStoreTests() => _store = new FileStore(_folder, _clock, _downloads);

    public void Dispose()
    {
        foreach (var folder in new[] { _folder, _downloads })
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
    }

    private void Download(string name, byte[] bytes, DateTimeOffset at)
    {
        var path = Path.Combine(_downloads, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, at.UtcDateTime);
    }

    [Fact]
    public void A_photo_or_PDF_saved_in_Downloads_is_listed_with_sent_files_by_time_and_read_from_there()
    {
        _store.Save("cat.jpg", "telegram", Photo);
        Download("invoice.pdf", Pdf, _clock.Now.AddMinutes(5));
        Download("older.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0], _clock.Now.AddMinutes(-5));

        var files = _store.Recent(10);

        Assert.Equal([("invoice.pdf", "pc"), ("cat.jpg", "telegram"), ("older.png", "pc")], files.Select(f => (f.Name, f.From)));
        Assert.Equal("invoice.pdf", _store.Recent(1, "").Single().Name); // "pull that up": the newest, wherever it is
        var found = _store.Find(files[0].Id)!;
        Assert.Equal(Pdf, File.ReadAllBytes(_store.PathOf(found)));
        Assert.Equal(found.Id, _store.Recent(1, "the invoice I just downloaded").Single().Id); // stable id
    }

    [Fact]
    public void Only_real_photos_and_PDFs_at_the_top_of_Downloads_count()
    {
        Download("notes.docx", Encoding.ASCII.GetBytes("PK\u0003\u0004"), _clock.Now);
        Download("fake.pdf", Encoding.ASCII.GetBytes("just text"), _clock.Now);
        Download("half.pdf.crdownload", Pdf, _clock.Now);
        Download(Path.Combine("sub", "deep.pdf"), Pdf, _clock.Now);
        Download("empty.jpg", [], _clock.Now);

        Assert.Empty(_store.Recent(10));
    }

    [Fact]
    public void A_download_deleted_or_changed_is_no_longer_found_by_its_old_id()
    {
        Download("invoice.pdf", Pdf, _clock.Now);
        var id = _store.Recent(1).Single().Id;

        Download("invoice.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 a new version"), _clock.Now.AddMinutes(1));
        Assert.Null(_store.Find(id));
        File.Delete(Path.Combine(_downloads, "invoice.pdf"));
        Assert.Empty(_store.Recent(10));
    }

    [Fact]
    public void Photos_and_PDFs_are_kept_listed_newest_first_and_read_back()
    {
        var photo = _store.Save("cat.jpg", "telegram", Photo);
        _clock.Now = _clock.Now.AddMinutes(1);
        var pdf = _store.Save("invoice.pdf", "deck", Pdf);

        Assert.Equal("Saved “invoice.pdf”.", pdf.Message);
        Assert.Equal([("invoice.pdf", "application/pdf", "deck"), ("cat.jpg", "image/jpeg", "telegram")],
            _store.Recent(10).Select(f => (f.Name, f.Type, f.From)));
        Assert.Equal(Pdf, File.ReadAllBytes(_store.PathOf(_store.Find(pdf.File!.Id)!)));
        Assert.Equal(Photo.Length, _store.Find(photo.File!.Id)!.Size);
    }

    [Theory]
    [InlineData("PK\u0003\u0004word/document.xml", "I can only keep photos and PDFs, and that file is neither.")] // a .docx
    [InlineData("<svg onload=\"alert(1)\"/>", "I can only keep photos and PDFs, and that file is neither.")]
    [InlineData("", "That file is empty.")]
    public void Anything_else_is_refused_and_nothing_is_written(string content, string message)
    {
        var saved = _store.Save("invoice.pdf", "deck", Encoding.ASCII.GetBytes(content)); // the name doesn't decide

        Assert.Equal((false, message), (saved.Ok, saved.Message));
        Assert.False(Directory.Exists(_folder) && Directory.EnumerateFiles(_folder).Any());
    }

    [Fact]
    public void A_file_over_20_MB_is_refused()
    {
        var big = new byte[FileStore.MaxBytes + 1];
        Photo.CopyTo(big, 0);

        Assert.Equal("That file is over 20 MB, too big to keep.", _store.Save("big.jpg", "telegram", big).Message);
    }

    [Theory]
    [InlineData("C:\\Users\\me\\Desktop\\invoice.pdf", "invoice.pdf")]
    [InlineData("../../etc/passwd.pdf", "passwd.pdf")]
    [InlineData("bad\u0007\nname.pdf", "badname.pdf")]
    [InlineData("   ", "document.pdf")]
    public void A_name_keeps_only_the_file_name(string sent, string kept) =>
        Assert.Equal(kept, _store.Save(sent, "deck", Pdf).File!.Name);

    [Fact]
    public void A_long_name_is_cut_but_keeps_its_extension()
    {
        var name = _store.Save(new string('a', 150) + ".pdf", "deck", Pdf).File!.Name;

        Assert.Equal(100, name.Length);
        Assert.EndsWith("a.pdf", name);
    }

    [Theory]
    [InlineData("", "receipt.jpg")]
    [InlineData("it", "receipt.jpg")]
    [InlineData("the last one I sent", "receipt.jpg")]
    [InlineData("invoice", "Invoice-Nov.pdf")]
    [InlineData("the invoices", "Invoice-Nov.pdf")]
    [InlineData("pdf", "Invoice-Nov.pdf")]
    [InlineData("the photo", "receipt.jpg")]
    [InlineData("cat picture", "cat.png")]
    [InlineData("receipt pdf", null)]
    [InlineData("tax return", null)]
    public void Words_pick_the_newest_file_that_matches(string words, string? name)
    {
        _store.Save("cat.png", "deck", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]);
        _clock.Now = _clock.Now.AddMinutes(1);
        _store.Save("Invoice-Nov.pdf", "deck", Pdf);
        _clock.Now = _clock.Now.AddMinutes(1);
        _store.Save("receipt.jpg", "telegram", Photo);

        Assert.Equal(name, _store.Recent(1, words).SingleOrDefault()?.Name);
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("0123456789ABCDEF")]
    [InlineData("0123456789abcdef")] // well formed, but never made here
    public void Only_ids_this_store_made_are_found(string id)
    {
        _store.Save("cat.jpg", "telegram", Photo);

        Assert.Null(_store.Find(id));
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0 }, "image/gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50, 0 }, "image/webp")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x41, 0x56, 0x45, 0 }, null)] // a WAV
    public void The_bytes_say_what_a_file_is(byte[] bytes, string? type) => Assert.Equal(type, FileStore.Sniff(bytes));
}
