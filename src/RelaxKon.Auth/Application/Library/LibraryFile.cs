namespace RelaxKon.Auth.Application.Library;

public sealed class LibraryFile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int AccountId { get; set; }
    public string Name { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public byte[] Content { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public long Version { get; set; } = 1;
}

public sealed record LibraryItem(string Id, string Name, string ContentType, long Size,
    DateTime CreatedAt, DateTime UpdatedAt, long Version);
public sealed record LibraryListing(IReadOnlyList<LibraryItem> Items, long UsedBytes, long MaxBytes,
    long MaxFileBytes, int MaxFiles);
public sealed record RenameLibraryFile(string Name, long Version);

public sealed class LibraryOptions
{
    public const long HardFileLimit = 10 * 1024 * 1024;
    public long MaxFileBytes { get; set; } = HardFileLimit;
    public long MaxBytes { get; set; } = 100 * 1024 * 1024;
    public int MaxFiles { get; set; } = 100;
    public bool IsValid() => MaxFileBytes is > 0 and <= HardFileLimit && MaxBytes >= MaxFileBytes
        && MaxBytes <= 1024L * 1024 * 1024 && MaxFiles is > 0 and <= 1000;
}

// Single-instance bounded admission: never build an unbounded waiter queue or one lock per account.
public sealed class LibraryAdmission : IDisposable
{
    public SemaphoreSlim Uploads { get; } = new(2, 2);
    public SemaphoreSlim Writes { get; } = new(1, 1);
    public SemaphoreSlim Reads { get; } = new(4, 4);
    public void Dispose() { Writes.Dispose(); Reads.Dispose(); Uploads.Dispose(); }
}

// MVC disposes FileStreamResult after the response, keeping slow downloads inside the cap.
internal sealed class LibraryDownloadStream(byte[] content, SemaphoreSlim gate) : MemoryStream(content, writable: false)
{
    private int released;
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Interlocked.Exchange(ref released, 1) == 0) gate.Release();
    }
    public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
