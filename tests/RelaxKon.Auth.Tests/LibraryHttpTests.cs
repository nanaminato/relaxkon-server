using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using RelaxKon.Auth.Application.Library;
using RelaxKon.Auth.Controllers.Files;
using RelaxKon.Auth.Data.Contexts;
using RelaxKon.Auth.Models.Accounts;

namespace RelaxKon.Auth.Tests;

[NonParallelizable]
public class LibraryHttpTests
{
    [Test] public async Task Real_http_requires_auth_and_download_is_private_attachment()
    {
        var path = Path.Combine(Path.GetTempPath(), $"library-http-{Guid.NewGuid():N}.db");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(LibraryController).Assembly).AddNewtonsoftJson();
        builder.Services.AddDbContext<AuthContext>(o => o.UseSqlite($"Data Source={path}"));
        builder.Services.AddSingleton<LibraryAdmission>(); builder.Services.AddScoped<LibraryUploadAdmissionFilter>();
        builder.Services.AddOptions<LibraryOptions>();
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, LibraryFixtureAuthentication>("fixture", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        try
        {
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AuthContext>();
                await db.Database.EnsureCreatedAsync();
                db.Accounts.AddRange(new Account { AccountId=1,Username="one",Password="fixture",Email="one@example.invalid" },
                    new Account { AccountId=2,Username="two",Password="fixture",Email="two@example.invalid" });
                await db.SaveChangesAsync();
            }
            app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            Assert.That((await client.GetAsync("api/library")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            client.DefaultRequestHeaders.Add("X-Test-Account", "1");
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent([1,2,3]), "file", "test.html");
            using var uploaded = await client.PostAsync("api/library", form);
            Assert.That(uploaded.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            var id = JObject.Parse(await uploaded.Content.ReadAsStringAsync())["id"]!.Value<string>();
            using var download = await client.GetAsync($"api/library/{id}/content");
            Assert.That(download.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await download.Content.ReadAsByteArrayAsync(), Is.EqualTo(new byte[] {1,2,3}));
            Assert.That(download.Content.Headers.ContentDisposition!.DispositionType, Is.EqualTo("attachment"));
            Assert.That(download.Headers.CacheControl!.NoStore, Is.True);
            Assert.That(download.Headers.GetValues("X-Content-Type-Options").Single(), Is.EqualTo("nosniff"));
            client.DefaultRequestHeaders.Remove("X-Test-Account"); client.DefaultRequestHeaders.Add("X-Test-Account", "2");
            Assert.That((await client.GetAsync($"api/library/{id}/content")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(JObject.Parse(await client.GetStringAsync("api/library"))["items"]!.Count(), Is.Zero);
        }
        finally
        {
            await app.StopAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}

public sealed class LibraryFixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var value = Request.Headers["X-Test-Account"].ToString();
        if (value is not ("1" or "2")) return Task.FromResult(AuthenticateResult.NoResult());
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, value)], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, Scheme.Name)));
    }
}
