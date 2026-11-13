using RelaxKon.Auth.Security.AuthSecurity;

namespace RelaxKon.Auth.Tests;

public class AuthSecurityStateTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }

    [Test]
    public void Defaults_are_enabled_bounded_and_have_expected_windows()
    {
        var settings = new AuthSecuritySettings();
        Assert.Multiple(() =>
        {
            Assert.That(settings.IsValid(), Is.True);
            Assert.That(settings.Enabled, Is.True);
            Assert.That(settings.IpLimit, Is.EqualTo(10));
            Assert.That(settings.IpWindowSeconds, Is.EqualTo(60));
            Assert.That(settings.AccountFailureLimit, Is.EqualTo(5));
            Assert.That(settings.AccountWindowSeconds, Is.EqualTo(900));
            Assert.That(settings.PairFailureLimit, Is.EqualTo(5));
            Assert.That(settings.PairWindowSeconds, Is.EqualTo(300));
            Assert.That(settings.MaxCooldownSeconds, Is.EqualTo(900));
        });
    }

    [Test]
    public void Invalid_policies_cannot_disable_resource_bounds()
    {
        var good = new AuthSecuritySettings();
        foreach (var invalid in new[] { good with { Revision = -1 }, good with { IpLimit = 0 },
            good with { AccountFailureLimit = 0 }, good with { PairFailureLimit = 0 },
            good with { MaxEntries = 0 }, good with { MaxEntries = 100001 },
            good with { MaxConcurrentLogins = 0 }, good with { MaxCooldownSeconds = 901 },
            good with { InitialCooldownSeconds = 61, MaxCooldownSeconds = 60 },
            good with { EventCapacity = 5001 }, good with { RefreshWindowSeconds = 0 } })
            Assert.That(invalid.IsValid(), Is.False, invalid.ToString());
    }

    [Test]
    public void Account_keys_are_case_and_accent_normalized_but_never_expose_username()
    {
        using var state = new AuthSecurityState(new Clock());
        Assert.That(state.AccountKey(" Alice "), Is.EqualTo(state.AccountKey("ALICE")));
        Assert.That(state.AccountKey("Ålice"), Is.EqualTo(state.AccountKey("Alice")));
        Assert.That(state.AccountKey("Alice"), Does.Not.Contain("Alice"));
        using var restarted = new AuthSecurityState(new Clock());
        Assert.That(restarted.AccountKey("Alice"), Is.Not.EqualTo(state.AccountKey("Alice")), "Do not create a cross-restart username dictionary.");
    }

    [Test]
    public void Ip_request_budget_is_shared_across_accounts_and_resets_at_window()
    {
        var clock = new Clock(); using var state = new AuthSecurityState(clock); var settings = new AuthSecuritySettings();
        for (var i = 0; i < settings.IpLimit; i++) Assert.That(state.Request("login", "ip-a", settings.IpLimit, 60, settings), Is.Zero);
        Assert.That(state.Request("login", "ip-a", settings.IpLimit, 60, settings), Is.EqualTo(60));
        Assert.That(state.Request("login", "ip-b", settings.IpLimit, 60, settings), Is.Zero);
        clock.Advance(59);
        Assert.That(state.Request("login", "ip-a", settings.IpLimit, 60, settings), Is.EqualTo(1));
        clock.Advance(1);
        Assert.That(state.Request("login", "ip-a", settings.IpLimit, 60, settings), Is.Zero);
    }

    [Test]
    public async Task Parallel_requests_cannot_overrun_fixed_window_budget()
    {
        using var state = new AuthSecurityState(new Clock()); var settings = new AuthSecuritySettings();
        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => state.Request("login", "ip", 10, 60, settings))));
        Assert.That(results.Count(retry => retry == 0), Is.EqualTo(10));
        Assert.That(results.Count(retry => retry > 0), Is.EqualTo(190));
    }

    [Test]
    public void Pair_cooldown_does_not_extend_when_rejected_and_automatically_recovers()
    {
        var clock = new Clock(); using var state = new AuthSecurityState(clock); var settings = new AuthSecuritySettings();
        for (var i = 0; i < 5; i++) state.Failure("ip", "account", settings);
        Assert.That(state.BeginLogin("ip", "account", settings), Is.EqualTo((false, 30)));
        clock.Advance(20);
        Assert.That(state.BeginLogin("ip", "account", settings), Is.EqualTo((false, 10)));
        clock.Advance(10);
        Assert.That(state.BeginLogin("ip", "account", settings).Admitted, Is.True);
        state.EndLogin();
        Assert.That(state.Failure("ip", "account", settings), Is.EqualTo(60));
    }

    [Test]
    public void Pair_cooldown_progressively_increases_but_never_exceeds_fifteen_minutes()
    {
        var clock = new Clock(); using var state = new AuthSecurityState(clock);
        var settings = new AuthSecuritySettings { PairFailureLimit = 1, PairWindowSeconds = 86400, AccountFailureLimit = 1000 };
        foreach (var expected in new[] { 30, 60, 120, 240, 480, 900, 900 })
        {
            Assert.That(state.Failure("ip", "account", settings), Is.EqualTo(expected));
            Assert.That(state.BeginLogin("ip", "account", settings).RetryAfterSeconds, Is.EqualTo(expected));
            clock.Advance(expected);
            Assert.That(state.BeginLogin("ip", "account", settings).Admitted, Is.True); state.EndLogin();
        }
    }

    [Test]
    public void Saturated_ip_budget_is_visible_and_can_be_cleared_by_administrator()
    {
        using var state = new AuthSecurityState(new Clock()); var settings = new AuthSecuritySettings();
        state.Request("login", "ip", 1, 60, settings);
        var restriction = state.Status(settings, []).Restrictions.Single();
        Assert.That(restriction.Dimension, Is.EqualTo("login-ip"));
        Assert.That(restriction.RetryAt, Is.Not.Null);
        state.Clear(restriction.Id);
        Assert.That(state.Request("login", "ip", 1, 60, settings), Is.Zero);
    }

    [Test]
    public void Account_pressure_limits_cross_ip_guesses_but_allows_bounded_correct_password_probe()
    {
        var clock = new Clock(); using var state = new AuthSecurityState(clock); var settings = new AuthSecuritySettings();
        for (var i = 0; i < 5; i++) state.Failure("attacker-" + i, "victim", settings);
        Assert.That(state.Status(settings, []).Restrictions.Any(x => x.Dimension == "account-soft"), Is.True);
        Assert.That(state.BeginLogin("legitimate-ip", "victim", settings), Is.EqualTo((false, 2)));
        clock.Advance(1);
        Assert.That(state.BeginLogin("another-attacker", "victim", settings), Is.EqualTo((false, 1)));
        clock.Advance(1);
        Assert.That(state.BeginLogin("legitimate-ip", "victim", settings).Admitted, Is.True,
            "Rejected requests must not extend the short account probe interval.");
        Assert.That(state.BeginLogin("other-attacker", "victim", settings).Admitted, Is.False);
        state.Success("legitimate-ip", "victim", settings); state.EndLogin();
        Assert.That(state.Status(settings, []).Restrictions.Any(x => x.Dimension == "account-soft"), Is.False);
    }

    [Test]
    public void One_account_pair_does_not_lock_another_account_from_same_ip()
    {
        using var state = new AuthSecurityState(new Clock()); var settings = new AuthSecuritySettings();
        for (var i = 0; i < 5; i++) state.Failure("shared-ip", "one", settings);
        Assert.That(state.BeginLogin("shared-ip", "one", settings).Admitted, Is.False);
        Assert.That(state.BeginLogin("shared-ip", "two", settings).Admitted, Is.True); state.EndLogin();
    }

    [Test]
    public void Successful_login_clears_pair_and_account_but_not_aggregate_ip_budget()
    {
        using var state = new AuthSecurityState(new Clock()); var settings = new AuthSecuritySettings();
        for (var i = 0; i < 10; i++) state.Request("login", "ip", 10, 60, settings);
        for (var i = 0; i < 5; i++) state.Failure("ip", "account", settings);
        state.Success("ip", "account", settings);
        Assert.That(state.BeginLogin("ip", "account", settings).Admitted, Is.True); state.EndLogin();
        Assert.That(state.Request("login", "ip", 10, 60, settings), Is.GreaterThan(0));
    }

    [Test]
    public void Pair_failures_expire_at_configured_window_not_maximum_cooldown()
    {
        var clock = new Clock(); using var state = new AuthSecurityState(clock);
        var settings = new AuthSecuritySettings { AccountFailureLimit = 100, PairWindowSeconds = 300 };
        for (var i = 0; i < 4; i++) Assert.That(state.Failure("ip", "account", settings), Is.Zero);
        clock.Advance(301);
        Assert.That(state.Failure("ip", "account", settings), Is.Zero, "A failure outside the 5-minute pair window must not become the fifth failure.");
    }

    [Test]
    public async Task Login_cpu_slots_are_bounded_even_if_rate_limits_are_disabled()
    {
        using var state = new AuthSecurityState(new Clock());
        var settings = new AuthSecuritySettings { Enabled = false, MaxConcurrentLogins = 4 };
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() => state.BeginLogin("ip" + i, "account" + i, settings))));
        Assert.That(results.Count(result => result.Admitted), Is.EqualTo(4));
        for (var i = 0; i < 4; i++) state.EndLogin();
        Assert.That(state.BeginLogin("next", "next", settings).Admitted, Is.True); state.EndLogin();
    }

    [Test]
    public void Cache_saturation_fails_closed_without_evicting_active_budgets_and_recovers_after_expiry()
    {
        var clock = new Clock(); using var state = new AuthSecurityState(clock);
        var settings = new AuthSecuritySettings { MaxEntries = 100 };
        for (var i = 0; i < 100; i++) Assert.That(state.Request("login", "ip" + i, 1, 60, settings), Is.Zero);
        Assert.That(state.Request("login", "overflow", 1, 60, settings), Is.GreaterThan(0));
        Assert.That(state.Request("login", "ip0", 1, 60, settings), Is.GreaterThan(0));
        Assert.That(state.Status(settings, []).EntryCount, Is.EqualTo(100));
        clock.Advance(60);
        Assert.That(state.Request("login", "overflow", 1, 60, settings), Is.Zero);
        Assert.That(state.Status(settings, []).EntryCount, Is.EqualTo(1));
    }

    [Test]
    public void Events_are_bounded_reconfiguration_is_immediate_and_restart_discards_counters()
    {
        using var state = new AuthSecurityState(new Clock()); var settings = new AuthSecuritySettings { EventCapacity = 10 };
        for (var i = 0; i < 50; i++) state.Record("test", "ip", null, 0, settings);
        state.Request("login", "ip", 1, 60, settings);
        Assert.That(state.Status(settings, []).Events.Count, Is.EqualTo(10));
        state.Reconfigure(settings with { IpLimit = 2 });
        Assert.That(state.Status(settings, []).EntryCount, Is.Zero);
        using var restarted = new AuthSecurityState(new Clock());
        Assert.That(restarted.Status(settings, []).EntryCount, Is.Zero);
        Assert.That(restarted.Status(settings, []).Events, Is.Empty);
    }

    [Test]
    public void Administrator_can_clear_visible_pair_restriction()
    {
        using var state = new AuthSecurityState(new Clock()); var settings = new AuthSecuritySettings { AccountFailureLimit = 100 };
        for (var i = 0; i < 5; i++) state.Failure("ip", "account", settings);
        var pair = state.Status(settings, []).Restrictions.Single(item => item.Dimension == "ip-account");
        state.Clear(pair.Id);
        Assert.That(state.BeginLogin("ip", "account", settings).Admitted, Is.True); state.EndLogin();
    }
}
