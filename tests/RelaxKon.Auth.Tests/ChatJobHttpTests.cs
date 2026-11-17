using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using RelaxKon.Auth.Models.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RelaxKon.Auth.Application.Chat;
using RelaxKon.Auth.Application.Routing;
using RelaxKon.Auth.Bootstrap;
using RelaxKon.Auth.Controllers.AI.Chat;
using RelaxKon.Auth.Data;
using RelaxKon.Auth.Data.Contexts;
using RelaxKon.Auth.Repositories.Catalog;
using RelaxKon.Auth.Security.FeatureAccess;

namespace RelaxKon.Auth.Tests;

public class ChatJobHttpTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Replays_committed_frames_after_disconnect_or_restart(bool restart)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        var path = Path.Combine(Path.GetTempPath(), $"chat-jobs-{Guid.NewGuid():N}.db");
        var connection = $"Data Source={path}";
        var chat = new PausedChat();
        var jobId = Guid.NewGuid();
        var endpoint = "api/ai/chat/jobs/" + jobId;
        var app = Host(connection, chat, sqlite: true);
        try
        {
            var database = DatabaseProvider.FromConfiguration(app.Configuration);
            await new DatabaseBootstrapper().RunAsync(database, new BootstrapAdmin("owner", "owner@example.invalid", "Fixture123!"), true, token);
            await EnableChat(app, token);
            await app.StartAsync(token);
            using var owner = Client(app);
            using var submitted = await owner.PostAsJsonAsync(endpoint, new
            {
                model = "fixture", conversationId = "100", assistantMessageId = 202,
                messages = new[] { new { role = "user", content = "hello" } }
            }, token);
            Assert.That(submitted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            await ReadFirstAndDisconnect(app, endpoint, token);

            if (restart)
            {
                await app.StopAsync(token);
                await app.DisposeAsync();
                var after = new PausedChat();
                app = Host(connection, after, sqlite: true);
                await app.StartAsync(token);
                using var browser = Client(app);
                var recovery = await browser.GetStringAsync(endpoint, token);
                Assert.Multiple(() =>
                {
                    Assert.That(recovery, Does.Contain("first").And.Contain("chat_generation_interrupted").And.Not.Contain("second"));
                    Assert.That(after.Calls, Is.Zero);
                });
                var listed = await browser.GetStringAsync("api/ai/chat/jobs?historyId=100", token);
                Assert.That(listed, Does.Contain(jobId.ToString()).And.Contain("202"));
                using var other = Client(app, "2");
                Assert.That(await other.GetStringAsync("api/ai/chat/jobs?historyId=100", token), Is.EqualTo("[]"));
            }
            else
            {
                chat.Continue.TrySetResult();
                var resumed = await owner.GetStringAsync(endpoint + "?offset=1", token);
                Assert.That(resumed, Does.Contain("second").And.Contain("completed").And.Not.Contain("first"));
            }
            Assert.That(chat.Calls, Is.EqualTo(1));
        }
        finally
        {
            chat.Continue.TrySetResult();
            await app.StopAsync();
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Test, Explicit("Requires TURBO_TEST_MYSQL and a disposable database.")]
    public async Task Mysql_recovers_unsent_job_and_keeps_reply_after_restart()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        await using var fixture = await MySqlTestDatabase.Create(legacy: true);
        await fixture.Execute("INSERT INTO Roles(Code,Name) VALUES('admin','Admin'); INSERT INTO AccountRoles(AccountId,RoleId) SELECT 1,RoleId FROM Roles WHERE Code='admin'");
        await using var connection = fixture.Connection();
        var chat = new PausedChat();
        var id = Guid.NewGuid();
        var endpoint = "api/ai/chat/jobs/" + id;
        var crashedId = Guid.NewGuid();
        var crashedEndpoint = "api/ai/chat/jobs/" + crashedId;
        var app = Host(connection.ConnectionString, chat, sqlite: false);
        try
        {
            await EnableChat(app, token);
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AuthContext>();
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                db.Add(new ChatGeneration
                {
                    AccountId = 1, Id = id.ToString("N"), CreatedAt = now - 45, LeaseUntil = now - 1,
                    HistoryId = 100, MessageId = 202, Model = "fixture",
                    RequestJson = JsonSerializer.Serialize(new ChatRequest { AccountId = 1, Model = "fixture" })
                });
                db.Add(new ChatGeneration
                {
                    AccountId = 1, Id = crashedId.ToString("N"), State = "running", CreatedAt = now - 45,
                    LeaseUntil = now - 1, HistoryId = 100, MessageId = 203, FrameCount = 1, OutputBytes = 12
                });
                db.Add(new ChatGenerationFrame
                {
                    AccountId = 1, JobId = crashedId.ToString("N"), Sequence = 0,
                    Payload = JsonSerializer.Serialize(new RelaxKon.Auth.Contracts.Chat.ChatStreamEventResponse("delta", Text: "committed before crash"))
                });
                await db.SaveChangesAsync(token);
            }
            await app.StartAsync(token);
            chat.Continue.TrySetResult();
            using var first = Client(app);
            var completed = await first.GetStringAsync(endpoint, token);
            Assert.That(completed, Does.Contain("first").And.Contain("second").And.Contain("completed"));
            Assert.That(chat.Calls, Is.EqualTo(1));
            Assert.That(await first.GetStringAsync(crashedEndpoint, token),
                Does.Contain("committed before crash").And.Contain("chat_generation_interrupted"));
            await app.StopAsync(token);
            await app.DisposeAsync();

            var after = new PausedChat();
            app = Host(connection.ConnectionString, after, sqlite: false);
            await app.StartAsync(token);
            using var reopened = Client(app);
            Assert.That(await reopened.GetStringAsync(endpoint + "?offset=1", token),
                Does.Contain("second").And.Contain("completed").And.Not.Contain("first"));
            Assert.That(after.Calls, Is.Zero);
            Assert.That(await fixture.Scalar<long>("SELECT DayCount FROM FeatureAccessUsage WHERE AccountId=1 AND FeatureKey='chat'"),
                Is.EqualTo(0));
            await using var syncContext = fixture.Auth();
            await new RelaxKon.Auth.Application.Sync.DataSyncService(syncContext).Apply(1,
                new(Guid.NewGuid(), "history", "delete", "100"), token);
            Assert.That(await reopened.GetStringAsync("api/ai/chat/jobs?historyId=100", token), Is.EqualTo("[]"));
            using var removed = await reopened.GetAsync(endpoint, token);
            Assert.That(removed.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally
        {
            chat.Continue.TrySetResult();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static WebApplication Host(string connection, IChatService chat, bool sqlite)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Database:Provider"] = sqlite ? "Sqlite" : "MySql";
        builder.Configuration["ConnectionStrings:ciko"] = connection;
        var database = DatabaseProvider.FromConfiguration(builder.Configuration);
        builder.Services.AddControllers().AddApplicationPart(typeof(ChatController).Assembly).AddNewtonsoftJson();
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, LibraryFixtureAuthentication>("fixture", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<AuthContext>(database.Configure);
        builder.Services.AddDbContext<KeyContext>(database.Configure);
        builder.Services.AddScoped<IModelRepository, ModelRepository>();
        builder.Services.AddSingleton<QuickModel>();
        builder.Services.AddSingleton(chat);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ChatJobs>();
        builder.Services.AddScoped<ChatJobRunner>();
        builder.Services.AddHostedService<ChatJobRecovery>();
        builder.Services.AddScoped<FeatureIdentity>();
        builder.Services.AddScoped<FeatureAccessStore>();
        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<FeatureAccessMiddleware>();
        app.MapControllers();
        return app;
    }

    private static Task EnableChat(WebApplication app, CancellationToken token)
    {
        var policy = FeaturePolicies.Empty;
        policy.Features.Single(feature => feature.Key == "chat").Roles["admin"] = new(true, 10, 10, 1);
        return new FeatureAccessStore(app.Configuration).Save(policy, token);
    }

    private static HttpClient Client(WebApplication app, string account = "1")
    {
        var origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var http = new HttpClient { BaseAddress = new Uri(origin) };
        http.DefaultRequestHeaders.Add("X-Test-Account", account);
        return http;
    }

    private static async Task ReadFirstAndDisconnect(WebApplication app, string endpoint, CancellationToken token)
    {
        using var streaming = Client(app);
        using var first = await streaming.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, token);
        using var reader = new StreamReader(await first.Content.ReadAsStreamAsync(token));
        Assert.That(await reader.ReadLineAsync(token), Does.Contain("first"));
    }

    private sealed class PausedChat : IChatService
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<string> Stream(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            Calls++;
            yield return "first";
            await Continue.Task.WaitAsync(token);
            yield return "second";
        }
    }
}
