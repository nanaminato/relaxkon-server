using Microsoft.AspNetCore.Builder;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKon.Auth.Contracts.Auth;
using RelaxKon.Auth.Controllers.Auth;
using RelaxKon.Auth.Data.Contexts;
using RelaxKon.Auth.Models.Accounts;
using RelaxKon.Auth.Models.Config;
using RelaxKon.Auth.Security;
using RelaxKon.Auth.Security.AuthSecurity;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace RelaxKon.Auth.Tests;

public class AuthLoginIntegrationTests
{
    private SqliteConnection connection = null!;
    private AuthContext database = null!;
    private AuthSecurityState state = null!;
    private readonly AccountPasswordService passwords = new();
    private Clock clock = null!;
    [SetUp] public async Task Setup()
    {
        connection = new("Data Source=:memory:"); await connection.OpenAsync();
        database = new(new DbContextOptionsBuilder<AuthContext>().UseSqlite(connection).Options, new ConfigurationBuilder().Build());
        await database.Database.EnsureCreatedAsync();
        var account = new Account { Username = "known", Email = "known@example.invalid" };
        account.Password = passwords.Hash(account, "GoodPassword123!");
        database.Accounts!.Add(account); await database.SaveChangesAsync();
        clock = new(); state = new(clock);
    }
    [TearDown] public async Task Dispose() { state.Dispose(); await database.DisposeAsync(); await connection.DisposeAsync(); }
    private AuthController Controller(string ip = "192.0.2.1", IAccountPasswordService? service = null)
    {
        var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return new(database, OptionsFactory.Create(new JwtSettings { SecretKey = new string('s', 64), Issuer = "test", Audience = "test" }),
            null!, service ?? passwords, new Sessions(), state) { ControllerContext = new() { HttpContext = context } };
    }
    private static LoginRequest Request(string user, string password = "incorrect") => new() { Username = user, Password = password };

    [Test] public async Task Wrong_and_unknown_users_have_identical_status_and_password_work_path()
    {
        var service = new CountingPasswords(passwords);
        Assert.That((await Controller(service: service).Login(Request("known"), default)).Result, Is.TypeOf<UnauthorizedResult>());
        Assert.That((await Controller(service: service).Login(Request("unknown"), default)).Result, Is.TypeOf<UnauthorizedResult>());
        Assert.That(service.Verifications, Is.EqualTo(2));
    }
    [Test] public async Task Correct_login_returns_tokens_and_clears_account_and_pair_failures()
    {
        await Controller().Login(Request("known"), default);
        var result = await Controller().Login(Request("known", "GoodPassword123!"), default);
        Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
        Assert.That(state.Status(new(), []).Restrictions, Is.Empty);
    }
    [Test] public async Task Email_and_username_share_the_same_login_failure_budget()
    {
        var controller = Controller();
        for (var attempt = 0; attempt < 5; attempt++)
            await controller.Login(Request(attempt % 2 == 0 ? "known" : "KNOWN@example.invalid"), default);
        var denied = await controller.Login(Request("known@example.invalid", "GoodPassword123!"), default);
        Assert.That(denied.Result, Is.TypeOf<ObjectResult>().With.Property("StatusCode").EqualTo(429));
        clock.Now = clock.Now.AddMinutes(16);
        var success = await controller.Login(Request("KNOWN@example.invalid", "GoodPassword123!"), default);
        Assert.That(success.Result, Is.TypeOf<OkObjectResult>());
    }
    [Test] public async Task Pair_limit_returns_429_retry_after_and_recovers_without_permanent_lock()
    {
        var controller = Controller();
        for (var i = 0; i < 5; i++) await controller.Login(Request("known"), default);
        Assert.That((await controller.Login(Request("known", "GoodPassword123!"), default)).Result, Is.TypeOf<ObjectResult>().With.Property("StatusCode").EqualTo(429));
        Assert.That(int.Parse(controller.Response.Headers.RetryAfter!), Is.GreaterThan(0));
        clock.Now = clock.Now.AddMinutes(16);
        Assert.That((await controller.Login(Request("known", "GoodPassword123!"), default)).Result, Is.TypeOf<OkObjectResult>());
    }
    [Test] public async Task Account_pressure_does_not_create_a_long_global_lock_for_correct_password()
    {
        for (var i = 0; i < 5; i++) await Controller($"192.0.2.{i+1}").Login(Request("known"), default);
        // Account-level probe is short and not extended by denied traffic.
        clock.Now = clock.Now.AddSeconds(6);
        Assert.That((await Controller("192.0.2.99").Login(Request("known", "GoodPassword123!"), default)).Result, Is.TypeOf<OkObjectResult>());
    }
    [Test] public async Task Database_collation_aliases_share_one_account_and_pair_budget()
    {
        // Model a collation alias not covered by Unicode normalization (as in MySQL ai_ci).
        connection.CreateCollation("NOCASE", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(
            left.Replace("ß", "ss"), right.Replace("ß", "ss")));
        var account = await database.Accounts!.SingleAsync(); account.Username = "straße";
        await database.SaveChangesAsync();
        var service = new CountingPasswords(passwords);
        Assert.That(state.AccountKey("straße"), Is.Not.EqualTo(state.AccountKey("strasse")));
        for (var i = 0; i < 5; i++)
            await Controller(service: service).Login(Request(i % 2 == 0 ? "straße" : "strasse"), default);
        var denied = await Controller(service: service).Login(Request("strasse", "GoodPassword123!"), default);
        Assert.That(denied.Result, Is.TypeOf<ObjectResult>().With.Property("StatusCode").EqualTo(429));
        Assert.That(service.Verifications, Is.EqualTo(5));
        clock.Now = clock.Now.AddSeconds(3);
        var success = await Controller("192.0.2.88", service).Login(Request("strasse", "GoodPassword123!"), default);
        Assert.That(success.Result, Is.TypeOf<OkObjectResult>());
    }

    [TestCase("192.0.2.10", "192.0.2.10")]
    [TestCase("10.0.0.1", "203.0.113.9")]
    public async Task Forwarded_ip_is_used_only_from_an_explicit_trusted_proxy(string peer, string expected)
    {
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
        options.KnownIPNetworks.Clear(); options.KnownProxies.Clear(); options.KnownProxies.Add(IPAddress.Parse("10.0.0.1"));
        var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.8,203.0.113.9";
        var forwarding = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, OptionsFactory.Create(options));
        await forwarding.Invoke(context);
        Assert.That(context.Connection.RemoteIpAddress!.ToString(), Is.EqualTo(expected));
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class CountingPasswords(IAccountPasswordService inner) : IAccountPasswordService
    {
        public int Verifications;
        public string Hash(Account account, string password) => inner.Hash(account, password);
        public PasswordVerificationState Verify(Account account, string password) { Verifications++; return inner.Verify(account, password); }
    }
    private sealed class Sessions : IAuthSessionService
    {
        public Task<RefreshTokenIssue> CreateSessionAsync(Account account, string? deviceName, string? ipAddress, CancellationToken cancellationToken = default) => Task.FromResult(new RefreshTokenIssue(Guid.NewGuid(), "test-refresh", DateTime.UtcNow.AddDays(1)));
        public Task<RefreshTokenRotation> RotateAsync(string rawToken, string? deviceName, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RevokeByRefreshTokenAsync(string rawToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RevokeSessionAsync(int accountId, Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task InvalidateAllAsync(int accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AuthSession>> GetActiveSessionsAsync(int accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsAccessTokenValidAsync(int accountId, string securityStamp, Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
