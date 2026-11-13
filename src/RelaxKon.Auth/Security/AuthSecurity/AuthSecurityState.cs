using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace RelaxKon.Auth.Security.AuthSecurity;

// One lock owns all counters, admission and capacity accounting. No network/DB work under this lock.
public sealed class AuthSecurityState(TimeProvider clock) : IDisposable
{
    private sealed class Entry(string dimension, DateTimeOffset expires)
    {
        public string Dimension = dimension;
        public int Count;
        public int Strikes;
        public DateTimeOffset Expires = expires;
        public DateTimeOffset? RetryAt;
        public DateTimeOffset CountUntil;
    }
    private readonly object gate = new();
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 100000 });
    private readonly HashSet<string> keys = new(StringComparer.Ordinal);
    private readonly Queue<SecurityEvent> events = new();
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    private int active;
    private DateTimeOffset nextPrune;
    public string AccountKey(string username)
    {
        var value = new string(username.Trim().Normalize(NormalizationForm.FormKD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToUpperInvariant();
        return Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(value)));
    }
    private void Prune(DateTimeOffset now)
    {
        foreach (var key in keys.ToArray())
            if (!cache.TryGetValue<Entry>(key, out var entry) || entry!.Expires <= now) { cache.Remove(key); keys.Remove(key); }
    }
    private Entry? Get(string key, string dimension, int seconds, AuthSecuritySettings settings, DateTimeOffset now)
    {
        if (cache.TryGetValue<Entry>(key, out var value) && value!.Expires > now) return value;
        cache.Remove(key); keys.Remove(key);
        if (keys.Count >= settings.MaxEntries)
        {
            if (now >= nextPrune) { Prune(now); nextPrune = now.AddSeconds(1); }
            if (keys.Count >= settings.MaxEntries) return null;
        }
        value = new Entry(dimension, now.AddSeconds(seconds));
        cache.Set(key, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(seconds) });
        keys.Add(key); return value;
    }
    private static int Retry(DateTimeOffset? at, DateTimeOffset now) => at > now ? Math.Max(1, (int)Math.Ceiling((at.Value - now).TotalSeconds)) : 0;
    public int Request(string group, string ip, int limit, int seconds, AuthSecuritySettings settings)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var entry = Get($"request:{group}:{ip}", group + "-ip", seconds, settings, now);
            if (entry is null) return 1;
            if (entry.Count >= limit) { entry.RetryAt = entry.Expires; return Retry(entry.Expires, now); }
            entry.Count++; if (entry.Count == limit) entry.RetryAt = entry.Expires; return 0;
        }
    }
    public bool BeginGlobalLogin(AuthSecuritySettings settings)
    {
        lock (gate)
        {
            if (active >= settings.MaxConcurrentLogins) return false;
            active++; return true;
        }
    }
    public (bool Admitted, int RetryAfterSeconds) BeginLogin(string ip, string account, AuthSecuritySettings settings)
    {
        lock (gate)
        {
            if (active >= settings.MaxConcurrentLogins) return (false, 1);
            var admission = AdmitAccount(ip, account, settings);
            if (admission.Admitted) active++;
            return admission;
        }
    }
    // Called after canonical database identity lookup, while holding a global login lease.
    public (bool Admitted, int RetryAfterSeconds) AdmitAccount(string ip, string account, AuthSecuritySettings settings)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            // Reserve both records before password work, fail closed on saturated memory.
            if (settings.Enabled)
            {
                var a = Get("account:" + account, "account-soft", settings.AccountWindowSeconds + settings.MaxCooldownSeconds, settings, now);
                var pair = Get("pair:" + ip + ":" + account, "ip-account", settings.PairWindowSeconds + settings.MaxCooldownSeconds, settings, now);
                if (a is null || pair is null) return (false, 1);
                var wait = Retry(pair.RetryAt, now);
                if (wait > 0) return (false, wait);
                if (a.CountUntil <= now) { a.Count = 0; a.RetryAt = null; }
                if (a.Count >= settings.AccountFailureLimit)
                {
                    wait = Retry(a.RetryAt, now);
                    if (wait > 0) return (false, wait);
                    // A bounded short probe interval throttles distributed guesses without a long account lock.
                    a.RetryAt = now.AddSeconds(settings.AccountProbeIntervalSeconds);
                }
            }
            return (true, 0);
        }
    }
    public void EndLogin() { lock (gate) active--; }
    public int Failure(string ip, string account, AuthSecuritySettings settings)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow(); var wait = 0;
            if (settings.Enabled)
            {
                var a = Get("account:" + account, "account-soft", settings.AccountWindowSeconds + settings.MaxCooldownSeconds, settings, now);
                var pair = Get("pair:" + ip + ":" + account, "ip-account", settings.PairWindowSeconds + settings.MaxCooldownSeconds, settings, now);
                foreach (var (key, entry, limit, window) in new[] { ("account:" + account, a, settings.AccountFailureLimit, settings.AccountWindowSeconds), ("pair:" + ip + ":" + account, pair, settings.PairFailureLimit, settings.PairWindowSeconds) })
                {
                    if (entry is null) { wait = Math.Max(wait, 1); continue; }
                    if (entry.CountUntil <= now) { entry.Count = 0; entry.Strikes = 0; entry.CountUntil = now.AddSeconds(window); }
                    entry.Count++;
                    if (entry.Count >= limit && Retry(entry.RetryAt, now) == 0)
                    {
                        entry.Strikes = Math.Min(20, entry.Strikes + 1);
                        var delay = entry.Dimension == "account-soft" ? settings.AccountProbeIntervalSeconds
                            : Math.Min(settings.MaxCooldownSeconds, (long)settings.InitialCooldownSeconds * (1L << (entry.Strikes - 1)));
                        entry.RetryAt = now.AddSeconds(delay);
                    }
                    var requiredExpiry = entry.RetryAt > entry.CountUntil ? entry.RetryAt.Value : entry.CountUntil;
                    if (requiredExpiry > entry.Expires)
                    {
                        entry.Expires = requiredExpiry;
                        cache.Set(key, entry, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = requiredExpiry - now });
                    }
                    wait = Math.Max(wait, Retry(entry.RetryAt, now));
                }
            }
            Add(new(now, "login-failed", ip, account, wait), settings); return wait;
        }
    }
    public void Success(string ip, string account, AuthSecuritySettings settings)
    {
        lock (gate)
        {
            Remove("account:" + account); Remove("pair:" + ip + ":" + account);
            // Never reset the IP request budget: success cannot bypass an IP's aggregate rate.
            Add(new(clock.GetUtcNow(), "login-succeeded", ip, account), settings);
        }
    }
    public void Record(string kind, string? ip, string? account, int retry, AuthSecuritySettings settings)
    { lock (gate) Add(new(clock.GetUtcNow(), kind, ip, account, retry), settings); }
    private void Add(SecurityEvent item, AuthSecuritySettings settings)
    { events.Enqueue(item); while (events.Count > settings.EventCapacity) events.Dequeue(); }
    private void Remove(string key) { cache.Remove(key); keys.Remove(key); }
    public void Clear(string id) { lock (gate) Remove(id); }
    public void Reconfigure(AuthSecuritySettings settings)
    {
        lock (gate)
        {
            // New policies take effect immediately; old-window counters must not impose stale limits.
            foreach (var key in keys) cache.Remove(key);
            keys.Clear(); while (events.Count > settings.EventCapacity) events.Dequeue();
        }
    }
    public AuthSecurityStatus Status(AuthSecuritySettings settings, IReadOnlyList<SecurityEvent> audit)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow(); Prune(now);
            var restrictions = keys.Select(key => (key, entry: cache.Get<Entry>(key))).Where(x => x.entry is not null && (x.entry.RetryAt > now || x.entry.Dimension == "account-soft" && x.entry.CountUntil > now && x.entry.Count >= settings.AccountFailureLimit))
                .Take(500).Select(x => new SecurityRestriction(x.key, x.entry!.Dimension, x.entry.Count, x.entry.Expires, x.entry.RetryAt)).ToList();
            return new(keys.Count, settings.MaxEntries, restrictions, events.Concat(audit).OrderByDescending(x => x.At).Take(settings.EventCapacity).ToList());
        }
    }
    public void Dispose() => cache.Dispose();
}
