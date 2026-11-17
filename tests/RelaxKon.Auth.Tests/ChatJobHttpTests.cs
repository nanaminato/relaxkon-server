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
    [Test]
    public async Task Dropped_connection_resumes_at_offset_without_regenerating()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var path = Path.Combine(Path.GetTempPath(), $"chat-jobs-{Guid.NewGuid():N}.db");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Database:Provider"] = "Sqlite";
        builder.Configuration["ConnectionStrings:ciko"] = $"Data Source={path}";
        var database = DatabaseProvider.FromConfiguration(builder.Configuration);
        var chat = new PausedChat();
        builder.Services.AddControllers().AddApplicationPart(typeof(ChatController).Assembly).AddNewtonsoftJson();
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, LibraryFixtureAuthentication>("fixture", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<AuthContext>(database.Configure);
        builder.Services.AddDbContext<KeyContext>(database.Configure);
        builder.Services.AddScoped<IModelRepository, ModelRepository>();
        builder.Services.AddSingleton<QuickModel>();
        builder.Services.AddSingleton<IChatService>(chat);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ChatJobs>();
        builder.Services.AddScoped<FeatureIdentity>();
        builder.Services.AddScoped<FeatureAccessStore>();
        await using var app = builder.Build();
        try
        {
            await new DatabaseBootstrapper().RunAsync(database, new BootstrapAdmin("owner", "owner@example.invalid", "Fixture123!"), true, token);
            var policy = FeaturePolicies.Empty;
            policy.Features.Single(feature => feature.Key == "chat").Roles["admin"] = new(true, 10, 10, 1);
            await new FeatureAccessStore(builder.Configuration).Save(policy, token);

            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseMiddleware<FeatureAccessMiddleware>();
            app.MapControllers();
            await app.StartAsync(token);
            var origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var owner = new HttpClient { BaseAddress = new Uri(origin) };
            owner.DefaultRequestHeaders.Add("X-Test-Account", "1");
            var job = "api/ai/chat/jobs/" + Guid.NewGuid();
            using var submitted = await owner.PostAsJsonAsync(job,
                new { model = "fixture", messages = new[] { new { role = "user", content = "hello" } } }, token);
            Assert.That(submitted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

            using (var streamClient = new HttpClient { BaseAddress = owner.BaseAddress })
            {
                streamClient.DefaultRequestHeaders.Add("X-Test-Account", "1");
                using var first = await streamClient.GetAsync(job, HttpCompletionOption.ResponseHeadersRead, token);
                using var reader = new StreamReader(await first.Content.ReadAsStreamAsync(token));
                Assert.That(await reader.ReadLineAsync(token), Does.Contain("first"));
                // The reader and HTTP client both go away before the provider returns the rest.
            }

            using var other = new HttpClient { BaseAddress = owner.BaseAddress };
            other.DefaultRequestHeaders.Add("X-Test-Account", "2");
            using var inaccessible = await other.GetAsync(job, token);
            Assert.That(inaccessible.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            chat.Continue.TrySetResult();
            using var reconnect = await owner.GetAsync(job + "?offset=1", token);
            var replay = await reconnect.Content.ReadAsStringAsync(token);
            Assert.Multiple(() =>
            {
                Assert.That(replay, Does.Contain("second").And.Contain("completed").And.Not.Contain("first"));
                Assert.That(chat.Calls, Is.EqualTo(1));
            });
        }
        finally
        {
            chat.Continue.TrySetResult();
            await app.StopAsync();
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
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
