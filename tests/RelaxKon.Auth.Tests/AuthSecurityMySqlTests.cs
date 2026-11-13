using Microsoft.Extensions.Configuration;
using RelaxKon.Auth.Security.AuthSecurity;

namespace RelaxKon.Auth.Tests;

[Explicit("Requires TURBO_TEST_MYSQL; creates and drops an isolated test database.")]
public sealed class AuthSecurityMySqlTests
{
    [Test]
    public async Task MySql_settings_round_trip_uses_same_policy_contract()
    {
        await using var fixture = await MySqlTestDatabase.Create();
        using var connection = fixture.Connection();
        var mysql = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:ciko"] = connection.ConnectionString }).Build();
        using var state = new AuthSecurityState(TimeProvider.System); var store = new AuthSecurityStore(mysql, state);
        var original = await store.Read(default);
        Assert.That(await store.Save(original with { PairFailureLimit = 7 }, 1, default), Is.True);
        var restarted = new AuthSecurityStore(mysql, state);
        Assert.That((await restarted.Read(default)).PairFailureLimit, Is.EqualTo(7));
        Assert.That((await restarted.Read(default)).Revision, Is.EqualTo(1));
        Assert.That(await fixture.Scalar<int>("SELECT COUNT(*) FROM FeatureAccessPolicies WHERE Id=2"), Is.EqualTo(1));
    }
}
