namespace RelaxKon.Auth.Security.AuthSecurity;

public sealed record AuthSecuritySettings
{
    public long Revision { get; init; }
    public bool Enabled { get; init; } = true;
    public int IpLimit { get; init; } = 10;
    public int IpWindowSeconds { get; init; } = 60;
    public int AccountFailureLimit { get; init; } = 5;
    public int AccountProbeIntervalSeconds { get; init; } = 2;
    public int AccountWindowSeconds { get; init; } = 900;
    public int PairFailureLimit { get; init; } = 5;
    public int PairWindowSeconds { get; init; } = 300;
    public int InitialCooldownSeconds { get; init; } = 30;
    public int MaxCooldownSeconds { get; init; } = 900;
    public int MaxEntries { get; init; } = 10000;
    public int MaxConcurrentLogins { get; init; } = 8;
    public int RegistrationLimit { get; init; } = 30;
    public int EmailLimit { get; init; } = 10;
    public int ResetLimit { get; init; } = 10;
    public int RefreshLimit { get; init; } = 60;
    public int VerificationLimit { get; init; } = 30;
    public int AnonymousWindowSeconds { get; init; } = 900;
    public int RefreshWindowSeconds { get; init; } = 60;
    public int EventCapacity { get; init; } = 500;
    public bool IsValid() => Revision >= 0 && IpLimit is >= 1 and <= 10000 && AccountFailureLimit is >= 1 and <= 1000 &&
        AccountProbeIntervalSeconds is >= 1 and <= 5 && PairFailureLimit is >= 1 and <= 1000 && IpWindowSeconds is >= 1 and <= 86400 && AccountWindowSeconds is >= 1 and <= 86400 &&
        PairWindowSeconds is >= 1 and <= 86400 && InitialCooldownSeconds is >= 1 and <= 900 &&
        MaxCooldownSeconds >= InitialCooldownSeconds && MaxCooldownSeconds <= 900 && MaxEntries is >= 100 and <= 100000 &&
        MaxConcurrentLogins is >= 1 and <= 64 && RegistrationLimit is >= 1 and <= 10000 && EmailLimit is >= 1 and <= 10000 &&
        ResetLimit is >= 1 and <= 10000 && RefreshLimit is >= 1 and <= 10000 && VerificationLimit is >= 1 and <= 10000 &&
        AnonymousWindowSeconds is >= 1 and <= 86400 && RefreshWindowSeconds is >= 1 and <= 86400 && EventCapacity is >= 10 and <= 5000;
}
public sealed record SecurityEvent(DateTimeOffset At, string Kind, string? Ip, string? AccountKey, int RetryAfterSeconds = 0, int? ActorId = null);
public sealed record SecurityRestriction(string Id, string Dimension, int Count, DateTimeOffset ExpiresAt, DateTimeOffset? RetryAt);
public sealed record AuthSecurityStatus(int EntryCount, int Capacity, IReadOnlyList<SecurityRestriction> Restrictions, IReadOnlyList<SecurityEvent> Events);
