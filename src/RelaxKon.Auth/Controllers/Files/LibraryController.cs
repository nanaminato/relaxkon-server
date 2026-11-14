using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelaxKon.Auth.Application.Library;
using RelaxKon.Auth.Data.Contexts;

namespace RelaxKon.Auth.Controllers.Files;

[Authorize]
[ApiController]
[Route("api/library")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LibraryController(AuthContext database, IOptions<LibraryOptions> options,
    LibraryAdmission admission) : ControllerBase
{
    private const int RetainedRevisions = 10;
    private const string DocumentType = "text/vnd.relaxkon.document+markdown";
    private int AccountId => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id > 0 ? id : 0;
    private IQueryable<LibraryFile> Owned => database.Set<LibraryFile>().Where(file => file.AccountId == AccountId);
    private static LibraryItem Item(LibraryFile f) => new(f.Id, f.Name, f.ContentType, f.Size, f.CreatedAt, f.UpdatedAt, f.Version);
    private IActionResult Busy()
    {
        Response.Headers.RetryAfter = "1";
        return Problem(statusCode: 429, title: "Library is busy. Please retry shortly.");
    }

    private async Task<LibraryLimits> Limits(CancellationToken token)
    {
        return await database.Set<LibraryQuota>().AsNoTracking().Where(row => row.Id == 1)
            .Select(row => new LibraryLimits(row.MaxBytes, row.MaxFileBytes)).SingleOrDefaultAsync(token)
            ?? new LibraryLimits(options.Value.MaxBytes, options.Value.MaxFileBytes);
    }

    [HttpGet("limits"), Authorize(Policy = "admin")]
    public async Task<LibraryLimits> GetLimits(CancellationToken token) => await Limits(token);

    [HttpPut("limits"), Authorize(Policy = "admin")]
    public async Task<IActionResult> SetLimits(LibraryLimits request, CancellationToken token)
    {
        if (!(new LibraryOptions { MaxBytes = request.MaxBytes, MaxFileBytes = request.MaxFileBytes }).IsValid())
            return BadRequest();
        if (!await admission.Writes.WaitAsync(0, token)) return Busy();
        try
        {
            var quota = await database.Set<LibraryQuota>().FindAsync([1], token);
            if (quota is null)
            {
                quota = new LibraryQuota();
                database.Add(quota);
            }
            quota.MaxBytes = request.MaxBytes;
            quota.MaxFileBytes = request.MaxFileBytes;
            await database.SaveChangesAsync(token);
            return Ok(request);
        }
        finally { admission.Writes.Release(); }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token)
    {
        if (AccountId == 0) return Unauthorized();
        if (!await admission.Reads.WaitAsync(0, token)) return Busy();
        try
        {
            // Project metadata only: opening the library never fetches every binary into memory.
            var items = await Owned.AsNoTracking().OrderByDescending(f => f.UpdatedAt).ThenBy(f => f.Id)
                .Select(f => new LibraryItem(f.Id, f.Name, f.ContentType, f.Size, f.CreatedAt, f.UpdatedAt, f.Version))
                .ToListAsync(token);
            var limits = await Limits(token);
            return Ok(new LibraryListing(items, items.Sum(f => f.Size), limits.MaxBytes,
                limits.MaxFileBytes, options.Value.MaxFiles));
        }
        finally { admission.Reads.Release(); }
    }

    [HttpPost]
    [ServiceFilter(typeof(LibraryUploadAdmissionFilter))]
    [RequestSizeLimit(LibraryOptions.HardFileLimit + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = LibraryOptions.HardFileLimit + 65536)]
    public Task<IActionResult> Upload(IFormFile file, CancellationToken token) =>
        Store(file, null, null, document: false, token);

    [HttpPost("documents")]
    [ServiceFilter(typeof(LibraryUploadAdmissionFilter))]
    [RequestSizeLimit(LibraryOptions.HardFileLimit + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = LibraryOptions.HardFileLimit + 65536)]
    public Task<IActionResult> CreateDocument(IFormFile file, CancellationToken token) =>
        Store(file, null, null, document: true, token);

    [HttpPut("{id}/document")]
    [ServiceFilter(typeof(LibraryUploadAdmissionFilter))]
    [RequestSizeLimit(LibraryOptions.HardFileLimit + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = LibraryOptions.HardFileLimit + 65536)]
    public Task<IActionResult> UpdateDocument(string id, IFormFile file, [FromQuery] long version, CancellationToken token) =>
        version < 1 ? Task.FromResult<IActionResult>(BadRequest()) : Store(file, id, version, document: true, token);

    private async Task<IActionResult> Store(IFormFile file, string? id, long? version, bool document, CancellationToken token)
    {
        if (AccountId == 0) return Unauthorized();
        if ((!document && file.Length < 1) || file.Length > LibraryOptions.HardFileLimit)
            return Problem(statusCode: 413, title: "File is empty or exceeds the library file limit.");
        var name = ValidateName(file.FileName);
        if (name is null) return Problem(statusCode: 400, title: "Invalid filename.");
        if (!await admission.Writes.WaitAsync(0, token)) return Busy();
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(token);
            var limits = await Limits(token);
            var existing = id is null ? null : await Owned.Where(f => f.Id == id)
                .Select(f => new { f.Size, f.Version, f.ContentType }).SingleOrDefaultAsync(token);
            if (id is not null)
            {
                if (existing is null || existing.ContentType != DocumentType) return NotFound();
                if (existing.Version != version) return Conflict();
            }
            var previousSize = existing?.Size ?? 0;
            if (file.Length > Math.Max(limits.MaxFileBytes, previousSize))
                return Problem(statusCode: 413, title: "File exceeds the library file limit.");
            var used = await Owned.SumAsync(f => (long?)f.Size, token) ?? 0;
            if ((id is null && await Owned.CountAsync(token) >= options.Value.MaxFiles)
                || (file.Length > previousSize && used - previousSize + file.Length > limits.MaxBytes))
                return Problem(statusCode: 507, title: "Library storage quota exceeded.");
            await using var source = file.OpenReadStream();
            using var buffer = new MemoryStream((int)file.Length);
            var chunk = new byte[65536];
            int length;
            while ((length = await source.ReadAsync(chunk, token)) != 0)
            {
                if (buffer.Length + length > file.Length)
                    return Problem(statusCode: 413, title: "File exceeds its declared size.");
                await buffer.WriteAsync(chunk.AsMemory(0, length), token);
            }
            if (buffer.Length != file.Length) return Problem(statusCode: 400, title: "Incomplete file upload.");
            var contentType = file.ContentType.Length <= 100 && System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(file.ContentType, out var parsed)
                ? parsed.MediaType ?? "application/octet-stream" : "application/octet-stream";
            var content = buffer.ToArray();
            if (document)
            {
                try { _ = new System.Text.UTF8Encoding(false, true).GetCharCount(content); }
                catch (System.Text.DecoderFallbackException) { return BadRequest(); }
                contentType = DocumentType;
            }
            else if (string.Equals(contentType, DocumentType, StringComparison.OrdinalIgnoreCase))
            {
                contentType = "text/markdown";
            }

            if (id is not null)
            {
                var previous = await Owned.Where(f => f.Id == id && f.Version == version)
                    .Select(f => new LibraryRevision { FileId = f.Id, Version = f.Version, Name = f.Name,
                        Content = f.Content, UpdatedAt = f.UpdatedAt }).SingleOrDefaultAsync(token);
                if (previous is null) return Conflict();
                var updated = DateTime.UtcNow;
                var changed = await Owned.Where(f => f.Id == id && f.Version == version)
                    .ExecuteUpdateAsync(set => set.SetProperty(f => f.Name, name)
                        .SetProperty(f => f.Content, content).SetProperty(f => f.Size, file.Length)
                        .SetProperty(f => f.Version, f => f.Version + 1).SetProperty(f => f.UpdatedAt, updated), token);
                if (changed == 0) return Conflict();
                var revisions = database.Set<LibraryRevision>();
                revisions.Add(previous);
                await database.SaveChangesAsync(token);
                var expired = await revisions.Where(r => r.FileId == id).OrderByDescending(r => r.Version)
                    .Skip(RetainedRevisions).Select(r => r.Version).ToListAsync(token);
                if (expired.Count > 0)
                    await revisions.Where(r => r.FileId == id && expired.Contains(r.Version)).ExecuteDeleteAsync(token);
                var item = await Owned.Where(f => f.Id == id)
                    .Select(f => new LibraryItem(f.Id, f.Name, f.ContentType, f.Size, f.CreatedAt, f.UpdatedAt, f.Version))
                    .SingleAsync(token);
                await transaction.CommitAsync(token);
                return Ok(item);
            }

            var entity = new LibraryFile { AccountId = AccountId, Name = name, ContentType = contentType,
                Size = file.Length, Content = content };
            database.Add(entity);
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Created($"api/library/{entity.Id}/content", Item(entity));
        }
        finally { admission.Writes.Release(); }
    }

    private IQueryable<LibraryRevision> OwnedRevisions(string id) =>
        from revision in database.Set<LibraryRevision>()
        join file in Owned.Where(f => f.Id == id && f.ContentType == DocumentType)
            on revision.FileId equals file.Id
        select revision;

    [HttpGet("{id}/revisions")]
    public async Task<IActionResult> Revisions(string id, CancellationToken token)
    {
        if (AccountId == 0) return Unauthorized();
        if (!await admission.Reads.WaitAsync(0, token)) return Busy();
        try
        {
            if (!await Owned.AnyAsync(f => f.Id == id && f.ContentType == DocumentType, token)) return NotFound();
            return Ok(await OwnedRevisions(id).OrderByDescending(r => r.Version)
                .Select(r => new { r.Version, r.Name, r.UpdatedAt }).ToListAsync(token));
        }
        finally { admission.Reads.Release(); }
    }

    [HttpGet("{id}/revisions/{version:long}")]
    public async Task<IActionResult> Revision(string id, long version, CancellationToken token)
    {
        if (AccountId == 0) return Unauthorized();
        if (!await admission.Reads.WaitAsync(0, token)) return Busy();
        var transferred = false;
        try
        {
            var revision = await OwnedRevisions(id).AsNoTracking().SingleOrDefaultAsync(r => r.Version == version, token);
            if (revision is null) return NotFound();
            Response.Headers.XContentTypeOptions = "nosniff";
            var result = File(new LibraryDownloadStream(revision.Content, admission.Reads), DocumentType, revision.Name);
            transferred = true;
            return result;
        }
        finally { if (!transferred) admission.Reads.Release(); }
    }

    [HttpGet("{id}/content")]
    public async Task<IActionResult> Download(string id, CancellationToken token)
    {
        if (AccountId == 0) return Unauthorized();
        if (!await admission.Reads.WaitAsync(0, token)) return Busy();
        var transferred = false;
        try
        {
            var file = await Owned.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, token);
            if (file is null) return NotFound();
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.Headers.CacheControl = "private, no-store";
            var result = File(new LibraryDownloadStream(file.Content, admission.Reads), file.ContentType,
                file.Name, enableRangeProcessing: false);
            transferred = true;
            return result;
        }
        finally { if (!transferred) admission.Reads.Release(); }
    }

    [HttpGet("{id}/text-preview")]
    public async Task<IActionResult> PreviewWord(string id, [FromServices] IFileContentExtractor extractor, CancellationToken token)
    {
        if (AccountId == 0) return Unauthorized();
        if (!await admission.Reads.WaitAsync(0, token)) return Busy();
        try
        {
            var file = await Owned.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, token);
            if (file is null) return NotFound();
            if (!file.Name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) return StatusCode(415);
            try
            {
                var text = extractor.Extractor(new RelaxKon.Auth.Contracts.Files.FileExtractionRequest
                {
                    FileName = file.Name, FileSize = file.Size, FileContent = Convert.ToBase64String(file.Content)
                });
                return Ok(new RelaxKon.Auth.Contracts.Files.FileExtractionResponse(text));
            }
            catch (Exception error) when (error is InvalidDataException or System.Xml.XmlException)
            {
                return UnprocessableEntity();
            }
        }
        finally { admission.Reads.Release(); }
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Rename(string id, RenameLibraryFile request, CancellationToken token)
    {
        if (AccountId == 0) return Unauthorized();
        var name = ValidateName(request.Name);
        if (name is null || request.Version < 1) return Problem(statusCode: 400, title: "Invalid filename or version.");
        if (!await admission.Writes.WaitAsync(0, token)) return Busy();
        try
        {
            // Metadata-only update: do not load the file body for rename.
            var changed = await Owned.Where(f => f.Id == id && f.Version == request.Version)
                .ExecuteUpdateAsync(set => set.SetProperty(f => f.Name, name)
                    .SetProperty(f => f.UpdatedAt, DateTime.UtcNow).SetProperty(f => f.Version, f => f.Version + 1), token);
            if (changed == 0) return await Owned.AnyAsync(f => f.Id == id, token) ? Conflict() : NotFound();
            var item = await Owned.Where(f => f.Id == id).Select(f => new LibraryItem(f.Id, f.Name, f.ContentType,
                f.Size, f.CreatedAt, f.UpdatedAt, f.Version)).SingleAsync(token);
            return Ok(item);
        }
        finally { admission.Writes.Release(); }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromQuery] long version, CancellationToken token)
    {
        if (AccountId == 0) return Unauthorized();
        if (version < 1) return BadRequest();
        if (!await admission.Writes.WaitAsync(0, token)) return Busy();
        try
        {
            var changed = await Owned.Where(f => f.Id == id && f.Version == version).ExecuteDeleteAsync(token);
            if (changed == 0) return await Owned.AnyAsync(f => f.Id == id, token) ? Conflict() : NotFound();
            return NoContent();
        }
        finally { admission.Writes.Release(); }
    }

    internal static string? ValidateName(string? value)
    {
        var name = value?.Trim();
        return string.IsNullOrEmpty(name) || name.Length > 200 || name is "." or ".."
            || name.Any(c => char.IsControl(c) || c is '/' or '\\') ? null : name;
    }
}
