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
    private int AccountId => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id > 0 ? id : 0;
    private IQueryable<LibraryFile> Owned => database.Set<LibraryFile>().Where(file => file.AccountId == AccountId);
    private static LibraryItem Item(LibraryFile f) => new(f.Id, f.Name, f.ContentType, f.Size, f.CreatedAt, f.UpdatedAt, f.Version);
    private IActionResult Busy()
    {
        Response.Headers.RetryAfter = "1";
        return Problem(statusCode: 429, title: "Library is busy. Please retry shortly.");
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
            return Ok(new LibraryListing(items, items.Sum(f => f.Size), options.Value.MaxBytes,
                options.Value.MaxFileBytes, options.Value.MaxFiles));
        }
        finally { admission.Reads.Release(); }
    }

    [HttpPost]
    [ServiceFilter(typeof(LibraryUploadAdmissionFilter))]
    [RequestSizeLimit(LibraryOptions.HardFileLimit + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = LibraryOptions.HardFileLimit + 65536)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken token)
    {
        if (AccountId == 0) return Unauthorized();
        if (file.Length < 1 || file.Length > options.Value.MaxFileBytes)
            return Problem(statusCode: 413, title: "File is empty or exceeds the library file limit.");
        var name = ValidateName(file.FileName);
        if (name is null) return Problem(statusCode: 400, title: "Invalid filename.");
        if (!await admission.Writes.WaitAsync(0, token)) return Busy();
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(token);
            var count = await Owned.CountAsync(token);
            var used = await Owned.SumAsync(f => (long?)f.Size, token) ?? 0;
            if (count >= options.Value.MaxFiles || used > options.Value.MaxBytes - file.Length)
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
            var entity = new LibraryFile { AccountId = AccountId, Name = name, ContentType = contentType,
                Size = file.Length, Content = buffer.ToArray() };
            database.Add(entity);
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Created($"api/library/{entity.Id}/content", Item(entity));
        }
        finally { admission.Writes.Release(); }
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
