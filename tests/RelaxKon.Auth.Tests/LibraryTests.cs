using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RelaxKon.Auth.Application.Library;
using RelaxKon.Auth.Controllers.Files;
using RelaxKon.Auth.Data.Contexts;
using RelaxKon.Auth.Models.Accounts;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace RelaxKon.Auth.Tests;

public class LibraryTests
{
    private string path = null!;
    private LibraryAdmission admission = null!;
    [SetUp] public async Task Setup()
    {
        path = Path.Combine(Path.GetTempPath(), $"library-{Guid.NewGuid():N}.db");
        admission = new();
        await using var db = Database();
        await db.Database.EnsureCreatedAsync();
        db.Accounts.AddRange(new Account { AccountId = 1, Username = "one", Email = "one@example.invalid", Password = "test" },
            new Account { AccountId = 2, Username = "two", Email = "two@example.invalid", Password = "test" });
        await db.SaveChangesAsync();
    }
    [TearDown] public void Cleanup() { admission.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    private AuthContext Database() => new(new DbContextOptionsBuilder<AuthContext>().UseSqlite($"Data Source={path}").Options, new ConfigurationBuilder().Build());
    internal static LibraryController Controller(AuthContext db, LibraryAdmission gate, int account = 1, LibraryOptions? limits = null)
    {
        var context = new DefaultHttpContext();
        if (account > 0) context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account.ToString())], "test"));
        return new(db, OptionsFactory.Create(limits ?? new()), gate) { ControllerContext = new() { HttpContext = context } };
    }
    internal static FormFile UploadFile(byte[]? bytes = null, string name = "notes.txt")
    {
        bytes ??= [1, 2, 3];
        return new(new MemoryStream(bytes), 0, bytes.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = "text/plain" };
    }
    [Test] public async Task Owner_upload_roundtrip_and_other_device_reload_without_content_in_listing()
    {
        LibraryItem saved;
        await using (var first = Database())
            saved = (LibraryItem)((CreatedResult)await Controller(first, admission).Upload(UploadFile(), default)).Value!;
        await using var otherDevice = Database();
        var controller = Controller(otherDevice, admission);
        var listing = (LibraryListing)((OkObjectResult)await controller.List(default)).Value!;
        Assert.That(listing.Items.Single(), Is.EqualTo(saved));
        Assert.That(listing.UsedBytes, Is.EqualTo(3));
        var download = (FileStreamResult)await controller.Download(saved.Id, default);
        Assert.That(admission.Reads.CurrentCount, Is.EqualTo(3));
        using var buffer = new MemoryStream();
        await download.FileStream.CopyToAsync(buffer);
        Assert.That(buffer.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(download.FileDownloadName, Is.EqualTo("notes.txt"));
        Assert.That(controller.Response.Headers.CacheControl.ToString(), Is.EqualTo("private, no-store"));
        Assert.That(controller.Response.Headers.XContentTypeOptions.ToString(), Is.EqualTo("nosniff"));
        await download.FileStream.DisposeAsync();
        Assert.That(admission.Reads.CurrentCount, Is.EqualTo(4));
    }
    [Test] public async Task Owner_boundary_applies_to_list_download_rename_delete()
    {
        await using var db = Database();
        var owner = Controller(db, admission);
        var saved = (LibraryItem)((CreatedResult)await owner.Upload(UploadFile(), default)).Value!;
        var stranger = Controller(db, admission, 2);
        Assert.That(((LibraryListing)((OkObjectResult)await stranger.List(default)).Value!).Items, Is.Empty);
        Assert.That(await stranger.Download(saved.Id, default), Is.TypeOf<NotFoundResult>());
        Assert.That(await stranger.Rename(saved.Id, new("stolen.txt", 1), default), Is.TypeOf<NotFoundResult>());
        Assert.That(await stranger.Delete(saved.Id, 1, default), Is.TypeOf<NotFoundResult>());
        Assert.That((await db.Set<LibraryFile>().SingleAsync()).Name, Is.EqualTo("notes.txt"));
    }
    [Test] public async Task Missing_identity_is_unauthorized_on_every_action_and_controller_requires_authentication()
    {
        Assert.That(typeof(LibraryController).GetCustomAttributes(typeof(AuthorizeAttribute), true), Has.Length.EqualTo(1));
        await using var db = Database(); var controller = Controller(db, admission, 0);
        Assert.That(await controller.List(default), Is.TypeOf<UnauthorizedResult>());
        Assert.That(await controller.Upload(UploadFile(), default), Is.TypeOf<UnauthorizedResult>());
        Assert.That(await controller.Download("id", default), Is.TypeOf<UnauthorizedResult>());
        Assert.That(await controller.Rename("id", new("name", 1), default), Is.TypeOf<UnauthorizedResult>());
        Assert.That(await controller.Delete("id", 1, default), Is.TypeOf<UnauthorizedResult>());
    }
    [Test] public async Task Cross_device_stale_rename_and_delete_conflict_instead_of_lost_updates()
    {
        await using var db = Database(); var controller = Controller(db, admission);
        var saved = (LibraryItem)((CreatedResult)await controller.Upload(UploadFile(), default)).Value!;
        var renamed = (LibraryItem)((OkObjectResult)await controller.Rename(saved.Id, new("new.txt", 1), default)).Value!;
        Assert.That(renamed.Version, Is.EqualTo(2));
        await using var second = Database(); var other = Controller(second, admission);
        Assert.That(await other.Rename(saved.Id, new("old.txt", 1), default), Is.TypeOf<ConflictResult>());
        Assert.That(await other.Delete(saved.Id, 1, default), Is.TypeOf<ConflictResult>());
        Assert.That(await other.Delete(saved.Id, 2, default), Is.TypeOf<NoContentResult>());
        Assert.That(((LibraryListing)((OkObjectResult)await controller.List(default)).Value!).UsedBytes, Is.Zero);
    }
    [Test] public async Task Quotas_reject_excess_and_concurrent_admission_is_bounded()
    {
        var limits = new LibraryOptions { MaxFileBytes = 3, MaxBytes = 5, MaxFiles = 2 };
        await using var db = Database(); var controller = Controller(db, admission, limits: limits);
        Assert.That(await controller.Upload(UploadFile(), default), Is.TypeOf<CreatedResult>());
        Assert.That(((ObjectResult)await controller.Upload(UploadFile(), default)).StatusCode, Is.EqualTo(507));
        Assert.That(((ObjectResult)await controller.Upload(UploadFile([1,2,3,4]), default)).StatusCode, Is.EqualTo(413));
        await admission.Writes.WaitAsync();
        try
        {
            var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            {
                await using var concurrentDb = Database();
                return await Controller(concurrentDb, admission).Upload(UploadFile(), default);
            }));
            Assert.That(attempts.Cast<ObjectResult>().Select(r => r.StatusCode), Is.All.EqualTo(429));
        }
        finally { admission.Writes.Release(); }
        Assert.That(await db.Set<LibraryFile>().CountAsync(), Is.EqualTo(1));
    }
    [Test] public async Task File_count_limit_is_independent_of_bytes_and_released_after_delete()
    {
        await using var db = Database(); var controller = Controller(db, admission, limits: new() { MaxFiles = 1 });
        var saved = (LibraryItem)((CreatedResult)await controller.Upload(UploadFile(), default)).Value!;
        Assert.That(((ObjectResult)await controller.Upload(UploadFile(), default)).StatusCode, Is.EqualTo(507));
        await controller.Delete(saved.Id, 1, default);
        Assert.That(await controller.Upload(UploadFile(), default), Is.TypeOf<CreatedResult>());
    }
    [TestCase("../private.txt")][TestCase("folder\\file.txt")][TestCase("bad\nname")][TestCase(" ")]
    public async Task Filenames_cannot_inject_paths_or_headers(string name)
    {
        await using var db = Database();
        Assert.That(((ObjectResult)await Controller(db, admission).Upload(UploadFile(name: name), default)).StatusCode, Is.EqualTo(400));
    }
    [Test] public async Task Upload_resource_filter_rejects_before_model_binding_and_releases_after_failure()
    {
        var action = new Microsoft.AspNetCore.Mvc.ActionContext(new DefaultHttpContext(),
            new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var context = new Microsoft.AspNetCore.Mvc.Filters.ResourceExecutingContext(action,
            new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>(), new List<Microsoft.AspNetCore.Mvc.ModelBinding.IValueProviderFactory>());
        var filter = new LibraryUploadAdmissionFilter(admission);
        await admission.Uploads.WaitAsync(); await admission.Uploads.WaitAsync();
        var called = false;
        try
        {
            await filter.OnResourceExecutionAsync(context, () =>
            {
                called = true;
                return Task.FromResult(new Microsoft.AspNetCore.Mvc.Filters.ResourceExecutedContext(action,
                    new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>()));
            });
            Assert.That(called, Is.False);
            Assert.That(((ObjectResult)context.Result!).StatusCode, Is.EqualTo(429));
        }
        finally { admission.Uploads.Release(2); }
        Assert.ThrowsAsync<InvalidOperationException>(() => filter.OnResourceExecutionAsync(context,
            () => throw new InvalidOperationException("binding failed")));
        Assert.That(admission.Uploads.CurrentCount, Is.EqualTo(2));
    }
    [Test] public async Task Sqlite_additive_migration_preserves_existing_accounts_and_files()
    {
        await using var db = Database();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE LibraryFiles");
        await db.Database.ExecuteSqlRawAsync(MySqlTestDatabase.Resource("LibrarySqliteUpgrade.sql"));
        Assert.That(await db.Accounts.CountAsync(), Is.EqualTo(2));
        Assert.That(await Controller(db, admission).Upload(UploadFile(), default), Is.TypeOf<CreatedResult>());
    }
}

[TestFixture, Explicit("Requires isolated MySQL fixture")]
public class LibraryMySqlTests
{
    [Test] public async Task Binary_storage_owner_isolation_and_version_conflicts_work_on_mysql()
    {
        await using var fixture = await MySqlTestDatabase.Create();
        using var admission = new LibraryAdmission();
        await using var db = fixture.Auth();
        var controller = LibraryTests.Controller(db, admission);
        var file = (LibraryItem)((CreatedResult)await controller.Upload(LibraryTests.UploadFile(), default)).Value!;
        await using var second = fixture.Auth();
        Assert.That(((LibraryListing)((OkObjectResult)await LibraryTests.Controller(second, admission).List(default)).Value!).Items.Single().Id, Is.EqualTo(file.Id));
        Assert.That(await LibraryTests.Controller(second, admission, 2).Download(file.Id, default), Is.TypeOf<NotFoundResult>());
        Assert.That(await controller.Rename(file.Id, new("new.txt", 1), default), Is.TypeOf<OkObjectResult>());
        Assert.That(await controller.Delete(file.Id, 1, default), Is.TypeOf<ConflictResult>());
        Assert.That(await controller.Delete(file.Id, 2, default), Is.TypeOf<NoContentResult>());
    }
}
