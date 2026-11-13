using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using RelaxKon.Auth.Security.AuthSecurity;

namespace RelaxKon.Auth.Tests;

public class AuthSecurityPersistenceTests
{
    private string path = null!;
    private IConfiguration configuration = null!;

    [SetUp]
    public async Task Setup()
    {
        path = Path.Combine(Path.GetTempPath(), $"relaxkon-auth-security-{Guid.NewGuid():N}.db");
        configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite", ["ConnectionStrings:ciko"] = $"Data Source={path};Pooling=False"
        }).Build();
        await Execute("CREATE TABLE FeatureAccessPolicies(Id INTEGER PRIMARY KEY, Revision INTEGER NOT NULL, Document TEXT NOT NULL); INSERT INTO FeatureAccessPolicies VALUES(1,7,'{\"existing\":true}');");
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }

    private async Task Execute(string sql)
    {
        await using var connection = new SqliteConnection(configuration.GetConnectionString("ciko"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> Scalar(string sql)
    {
        await using var connection = new SqliteConnection(configuration.GetConnectionString("ciko"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    [Test]
    public async Task Settings_use_separate_existing_policy_row_survive_restart_and_preserve_feature_grants()
    {
        using var state = new AuthSecurityState(TimeProvider.System);
        var store = new AuthSecurityStore(configuration, state);
        var defaults = await store.Read(default);
        Assert.That(defaults, Is.EqualTo(new AuthSecuritySettings()));
        state.Request("login", "ip", 1, 60, defaults);
        Assert.That(await store.Save(defaults with { IpLimit = 23, Enabled = false }, 123, default), Is.True);
        Assert.That(state.Status(defaults, []).EntryCount, Is.Zero);
        using var restartedState = new AuthSecurityState(TimeProvider.System);
        var restarted = new AuthSecurityStore(configuration, restartedState);
        var persisted = await restarted.Read(default);
        Assert.Multiple(() =>
        {
            Assert.That(persisted.IpLimit, Is.EqualTo(23));
            Assert.That(persisted.Enabled, Is.False);
            Assert.That(persisted.Revision, Is.EqualTo(1));
            Assert.That(restarted.Audit.Single().ActorId, Is.EqualTo(123));
            Assert.That(restarted.Audit.Single().Kind, Is.EqualTo("settings-updated"));
            Assert.That(restartedState.Status(persisted, []).EntryCount, Is.Zero);
        });
        Assert.That(Convert.ToInt64(await Scalar("SELECT Revision FROM FeatureAccessPolicies WHERE Id=1")), Is.EqualTo(7));
        Assert.That(await Scalar("SELECT Document FROM FeatureAccessPolicies WHERE Id=1"), Is.EqualTo("{\"existing\":true}"));
        Assert.That(Convert.ToInt64(await Scalar("SELECT COUNT(*) FROM FeatureAccessPolicies")), Is.EqualTo(2));
    }

    [Test]
    public async Task Cold_configuration_load_does_not_queue_anonymous_waiters()
    {
        using var state = new AuthSecurityState(TimeProvider.System);
        var store = new AuthSecurityStore(configuration, state);
        var semaphore = (SemaphoreSlim)typeof(AuthSecurityStore).GetField("gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
        await semaphore.WaitAsync();
        try { Assert.ThrowsAsync<AuthSecurityUnavailableException>(() => store.Read(default)); }
        finally { semaphore.Release(); }
        Assert.That(await store.Read(default), Is.EqualTo(new AuthSecuritySettings()));
    }

    [Test]
    public async Task Optimistic_settings_revision_rejects_stale_writes()
    {
        using var state = new AuthSecurityState(TimeProvider.System);
        var store = new AuthSecurityStore(configuration, state);
        var original = await store.Read(default);
        Assert.That(await store.Save(original with { IpLimit = 20 }, 1, default), Is.True);
        Assert.That(await store.Save(original with { IpLimit = 99 }, 2, default), Is.False);
        Assert.That((await store.Read(default)).IpLimit, Is.EqualTo(20));
        Assert.That(store.Audit.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task Concurrent_same_revision_settings_writes_have_single_winner()
    {
        using var state = new AuthSecurityState(TimeProvider.System);
        var store = new AuthSecurityStore(configuration, state); var initial = await store.Read(default);
        var writes = await Task.WhenAll(Enumerable.Range(1, 20).Select(i => Task.Run(() => store.Save(initial with { IpLimit = i }, i, default))));
        Assert.That(writes.Count(saved => saved), Is.EqualTo(1));
        Assert.That((await store.Read(default)).Revision, Is.EqualTo(1));
    }

    [Test]
    public async Task Persistent_administrative_audit_has_fixed_retention_bound()
    {
        using var state = new AuthSecurityState(TimeProvider.System); var store = new AuthSecurityStore(configuration, state);
        for (var i = 0; i < 105; i++) Assert.That(await store.Save(await store.Read(default), i, default), Is.True);
        Assert.That(store.Audit.Count, Is.EqualTo(100));
        Assert.That(store.Audit[0].ActorId, Is.EqualTo(5));
        using var restartedState = new AuthSecurityState(TimeProvider.System); var restarted = new AuthSecurityStore(configuration, restartedState);
        await restarted.Read(default);
        Assert.That(restarted.Audit.Count, Is.EqualTo(100));
    }

    [Test]
    public async Task Invalid_persisted_or_submitted_settings_fail_closed()
    {
        using var state = new AuthSecurityState(TimeProvider.System); var store = new AuthSecurityStore(configuration, state);
        Assert.ThrowsAsync<ArgumentException>(() => store.Save(new() { IpLimit = 0 }, 1, default));
        await Execute("INSERT INTO FeatureAccessPolicies VALUES(2,0,'{\"Settings\":{\"IpLimit\":0},\"Audit\":[]}');");
        var second = new AuthSecurityStore(configuration, state);
        Assert.ThrowsAsync<InvalidOperationException>(() => second.Read(default));
        Assert.ThrowsAsync<InvalidOperationException>(() => second.Read(default), "Invalid settings must never be cached by a failed first read.");
    }

    [Test]
    public async Task Legacy_policy_read_exposes_management_and_old_client_save_preserves_explicit_revocation()
    {
        await Execute("CREATE TABLE Roles(RoleId INTEGER PRIMARY KEY,Code TEXT); INSERT INTO Roles VALUES(1,'admin'); DELETE FROM FeatureAccessPolicies WHERE Id=1;");
        var policyStore = new RelaxKon.Auth.Security.FeatureAccess.FeatureAccessStore(configuration);
        var policy = await policyStore.Read(default);
        policy.Features.Single(feature => feature.Key == "auth-security-management").Roles["admin"] = new(false);
        Assert.That(await policyStore.Save(policy, default), Is.True);
        var oldClient = (await policyStore.Read(default)) with { Features = policy.Features.Where(feature => feature.Key != "auth-security-management").ToList(), Revision = 1 };
        Assert.That(await policyStore.Save(oldClient, default), Is.True);
        var saved = await policyStore.Read(default);
        Assert.That(RelaxKon.Auth.Security.FeatureAccess.FeaturePolicies.Resolve(saved, "auth-security-management", ["admin"]).Enabled, Is.False);
    }

}
