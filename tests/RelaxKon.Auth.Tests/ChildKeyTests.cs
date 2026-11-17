using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RelaxKon.Auth.Application.Providers;
using RelaxKon.Auth.Data.Contexts;
using RelaxKon.Auth.Models.Accounts;
using RelaxKon.Auth.Models.Suppliers;
using RelaxKon.Auth.Security.ChildKeys;
using RelaxKon.Auth.Security.FeatureAccess;

namespace RelaxKon.Auth.Tests;

public class ChildKeyTests
{
    [Test]
    public async Task Issuer_owns_key_and_native_requests_use_bound_supplier_until_revoked()
    {
        var token = TestContext.CurrentContext.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(token);
        var configuration = new ConfigurationBuilder().Build();
        await using var accounts = new AuthContext(new DbContextOptionsBuilder<AuthContext>()
            .UseSqlite(connection).Options, configuration);
        await using var keys = new KeyContext(new DbContextOptionsBuilder<KeyContext>()
            .UseSqlite(connection).Options, configuration);
        await accounts.Database.EnsureCreatedAsync(token);
        await keys.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(token);
        var owner = new Account { Username = "owner", Email = "owner@example.invalid", Password = "unused" };
        var other = new Account { Username = "other", Email = "other@example.invalid", Password = "unused" };
        var role = new Role { Code = "admin", Name = "Admin" };
        accounts.AddRange(owner, other, role);
        await accounts.SaveChangesAsync(token);
        accounts.AddRange(new AccountRole { AccountId = owner.AccountId, RoleId = role.RoleId },
            new AccountRole { AccountId = other.AccountId, RoleId = role.RoleId });
        await accounts.SaveChangesAsync(token);

        var parent = new SupplierKey
        {
            BaseUrl = "https://upstream.example.invalid/custom/v1",
            ApiKey = "upstream-secret",
            RequestIdentifier = (int)HandlerType.Openai,
            Enable = true
        };
        keys.Add(parent);
        await keys.SaveChangesAsync(token);
        var ownerController = Controller(owner.AccountId, keys);
        var created = (ObjectResult)await ownerController.Create(new(parent.SupplierKeyId, "external", 30), token);
        Assert.That(created.StatusCode, Is.EqualTo(201));
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(created.Value));
        var secret = response.RootElement.GetProperty("secret").GetString()!;
        var stored = await keys.Set<ChildApiKey>().AsNoTracking().SingleAsync(token);
        Assert.That(stored.SecretHash, Is.EqualTo(ChildApiKey.Hash(secret)).And.Not.EqualTo(secret));

        var otherController = Controller(other.AccountId, keys);
        var otherKeys = (OkObjectResult)await otherController.List(token);
        Assert.That(otherKeys.Value, Is.Empty);
        Assert.That(await otherController.Revoke(stored.Id, token), Is.TypeOf<NotFoundResult>());

        var context = new DefaultHttpContext { RequestAborted = token };
        context.Request.Headers.Authorization = "Bearer " + secret;
        Assert.That((await Authenticate(context, keys, accounts)).Succeeded, Is.True);
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream("""{"stream":true}"""u8.ToArray());
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBody());
        context.Response.Body = new MemoryStream();
        using var upstream = new RecordingUpstream();
        var proxy = new NativeProviderController(upstream, NullLogger<NativeProviderController>.Instance) { ControllerContext = new() { HttpContext = context } };
        await proxy.Forward("v1/chat/completions");
        Assert.Multiple(() =>
        {
            Assert.That(upstream.Uri?.AbsoluteUri, Is.EqualTo("https://upstream.example.invalid/custom/v1/chat/completions"));
            Assert.That(upstream.Authorization, Is.EqualTo("Bearer upstream-secret"));
            Assert.That(upstream.Body, Is.EqualTo("""{"stream":true}"""));
            Assert.That(context.Response.ContentType, Does.StartWith("text/event-stream"));
            Assert.That(System.Text.Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()),
                Is.EqualTo(RecordingUpstream.Events));
        });

        context.Response.Headers.Clear();
        await proxy.Forward("v1/organization/admin_api_keys");
        Assert.That(context.Response.StatusCode, Is.EqualTo(404));
        Assert.That(upstream.Uri?.AbsolutePath, Is.EqualTo("/custom/v1/chat/completions"));

        context.Response.Headers.Clear();
        context.Response.Body = new MemoryStream();
        context.Request.Body = new MemoryStream("""{"prompt":"test"}"""u8.ToArray());
        await proxy.Forward("v1/images/generations");
        Assert.Multiple(() =>
        {
            Assert.That(upstream.Uri?.AbsolutePath, Is.EqualTo("/custom/v1/images/generations"));
            Assert.That(upstream.Body, Is.EqualTo("""{"prompt":"test"}"""));
            Assert.That(context.Response.StatusCode, Is.EqualTo(429));
            Assert.That(System.Text.Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()),
                Is.EqualTo(RecordingUpstream.ImageError));
        });

        parent.Enable = false;
        await keys.SaveChangesAsync(token);
        Assert.That((await Authenticate(context, keys, accounts)).Succeeded, Is.False);
        parent.Enable = true;
        await keys.SaveChangesAsync(token);

        var ownerRole = await accounts.AccountRoles.SingleAsync(link => link.AccountId == owner.AccountId, token);
        accounts.Remove(ownerRole);
        await accounts.SaveChangesAsync(token);
        Assert.That((await Authenticate(context, keys, accounts)).Succeeded, Is.False);
        accounts.Add(new AccountRole { AccountId = owner.AccountId, RoleId = role.RoleId });
        await accounts.SaveChangesAsync(token);

        Assert.That(await ownerController.Revoke(stored.Id, token), Is.TypeOf<NoContentResult>());
        Assert.That((await Authenticate(context, keys, accounts)).Succeeded, Is.False);
    }

    private static ChildKeyController Controller(int accountId, KeyContext keys)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, accountId.ToString())], "test"));
        return new ChildKeyController(keys, TimeProvider.System)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext { User = user } }
        };
    }

    private static async Task<AuthenticateResult> Authenticate(HttpContext context, KeyContext keys, AuthContext accounts)
    {
        var handler = new ChildKeyAuthentication(new SchemeOptions(), NullLoggerFactory.Instance, UrlEncoder.Default,
            keys, new FeatureIdentity(accounts), TimeProvider.System);
        await handler.InitializeAsync(new AuthenticationScheme(ChildKeyAuthentication.SchemeName, null,
            typeof(ChildKeyAuthentication)), context);
        return await handler.AuthenticateAsync();
    }

    private sealed class SchemeOptions : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private sealed class RequestBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class RecordingUpstream : HttpMessageHandler, IHttpClientFactory
    {
        public const string Events = "data: {\"delta\":\"native\"}\n\ndata: [DONE]\n\n";
        public const string ImageError = """{"error":{"message":"provider rate limit"}}""";
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(token);
            return Uri?.AbsolutePath.EndsWith("/images/generations", StringComparison.Ordinal) == true
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(ImageError, null, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Events, null, "text/event-stream") };
        }
    }
}
