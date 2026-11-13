using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RelaxKon.Auth.Data.Contexts;
using RelaxKon.Auth.Models.Accounts;
using RelaxKon.Auth.Security.AuthSecurity;
using RelaxKon.Auth.Security.FeatureAccess;

namespace RelaxKon.Auth.Tests;

public class AuthSecurityBoundaryTests
{
    private string path = null!;
    private AuthContext database = null!;
    private AuthSecurityState state = null!;
    private AuthSecurityStore store = null!;
    private FeatureAccessStore policies = null!;
    [SetUp] public async Task Setup()
    {
        path = Path.Combine(Path.GetTempPath(), "auth-boundary-" + Guid.NewGuid() + ".db");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Database:Provider"] = "Sqlite", ["ConnectionStrings:ciko"] = "Data Source=" + path }).Build();
        database = new(new DbContextOptionsBuilder<AuthContext>().UseSqlite("Data Source=" + path).Options, configuration);
        await database.Database.EnsureCreatedAsync();
        await database.Database.ExecuteSqlRawAsync("CREATE TABLE FeatureAccessPolicies(Id INTEGER PRIMARY KEY,Revision INTEGER NOT NULL,Document TEXT NOT NULL)");
        var user = new Account { AccountId = 1, Username = "admin", Email = "admin@example.invalid", Password = "fixture" };
        var role = new Role { RoleId = 1, Code = "admin", Name = "admin" };
        database.Accounts!.Add(user); database.Roles!.Add(role); database.AccountRoles!.Add(new AccountRole { AccountId = 1, RoleId = 1 });
        await database.SaveChangesAsync();
        state = new(TimeProvider.System); store = new(configuration, state); policies = new(configuration);
    }
    [TearDown] public async Task Cleanup() { state.Dispose(); await database.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    private AuthSecurityController Controller()
    {
        var context = new DefaultHttpContext(); context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,"1"),new Claim(ClaimTypes.Role,"admin")],"test"));
        return new(store,state,new FeatureIdentity(database),policies) { ControllerContext = new() { HttpContext = context } };
    }
    [Test] public async Task Administrator_can_live_update_and_nonadministrator_cannot_use_stale_admin_claim()
    {
        var controller = Controller();
        Assert.That(await controller.Put(new() { IpLimit=2 },default),Is.TypeOf<OkObjectResult>());
        Assert.That((await store.Read(default)).IpLimit,Is.EqualTo(2));
        await database.AccountRoles!.ExecuteDeleteAsync();
        Assert.That(await controller.Get(default),Is.TypeOf<ForbidResult>());
        Assert.That(await controller.Put(new() { Revision=1,IpLimit=99 },default),Is.TypeOf<ForbidResult>());
        Assert.That(await controller.Clear("anything",default),Is.TypeOf<ForbidResult>());
    }
    [Test] public async Task Disabling_dynamic_management_grant_denies_even_current_admin()
    {
        var snapshot = await policies.Read(default);
        var feature = snapshot.Features.Single(x=>x.Key=="auth-security-management");
        var updated = snapshot with { Features=snapshot.Features.Select(x=>x.Key==feature.Key ? x with { Roles=new() } : x).ToList() };
        Assert.That(await policies.Save(updated,default),Is.True);
        Assert.That(await Controller().Status(default),Is.TypeOf<ForbidResult>());
    }
    [TestCase("/api/auth/login",10)]
    [TestCase("/api/auth/refresh",60)]
    [TestCase("/api/auth/register/email-code",10)]
    [TestCase("/api/auth/password-reset/request",10)]
    [TestCase("/api/verification/generate",30)]
    public async Task Anonymous_routes_enforce_per_ip_limits_and_ignore_spoofed_headers(string path,int permits)
    {
        var calls=0; var middleware=new AuthSecurityMiddleware(_=>{calls++;return Task.CompletedTask;});
        for(var i=0;i<=permits;i++)
        {
            var context=new DefaultHttpContext(); context.Request.Path=path; context.Connection.RemoteIpAddress=IPAddress.Parse("192.0.2.1");
            context.Request.Headers["X-Forwarded-For"]=$"198.51.100.{i}"; context.Response.Body=new MemoryStream();
            await middleware.InvokeAsync(context,store,state);
            if(i==permits) { Assert.That(context.Response.StatusCode,Is.EqualTo(429)); Assert.That(int.Parse(context.Response.Headers.RetryAfter!),Is.GreaterThan(0)); }
        }
        Assert.That(calls,Is.EqualTo(permits));
    }
}
