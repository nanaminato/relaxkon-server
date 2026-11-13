using System.Text.Json;
using RelaxKon.Auth.Data;

namespace RelaxKon.Auth.Security.AuthSecurity;

public sealed record AuthSecurityDocument(AuthSecuritySettings Settings, List<SecurityEvent> Audit);
// A second singleton document in the existing policy table avoids a new schema/table.
public sealed class AuthSecurityStore(IConfiguration configuration, AuthSecurityState state)
{
    private readonly DatabaseProvider provider = DatabaseProvider.FromConfiguration(configuration);
    private readonly SemaphoreSlim gate = new(1, 1);
    private AuthSecurityDocument? document;
    public async Task<AuthSecuritySettings> Read(CancellationToken token)
    {
        if (Volatile.Read(ref document) is { } current) return current.Settings;
        await gate.WaitAsync(token);
        try
        {
            if (document is not null) return document.Settings;
            await using var connection = provider.CreateConnection(); await connection.OpenAsync(token);
            await using var command = provider.Command(connection, null, "SELECT Revision,Document FROM FeatureAccessPolicies WHERE Id=2");
            await using var reader = await command.ExecuteReaderAsync(token);
            var candidate = new AuthSecurityDocument(new(), []);
            if (await reader.ReadAsync(token))
            {
                var persisted = JsonSerializer.Deserialize<AuthSecurityDocument>(reader.GetString(1))
                    ?? throw new InvalidOperationException("Invalid persisted authentication security policy.");
                candidate = persisted with { Settings = persisted.Settings with { Revision = reader.GetInt64(0) } };
            }
            if (candidate.Settings is null || !candidate.Settings.IsValid() || candidate.Audit is null || candidate.Audit.Count > 100)
                throw new InvalidOperationException("Invalid persisted authentication security policy.");
            Volatile.Write(ref document, candidate);
            return candidate.Settings;
        }
        finally { gate.Release(); }
    }
    public IReadOnlyList<SecurityEvent> Audit => Volatile.Read(ref document)?.Audit ?? [];
    public async Task<bool> Save(AuthSecuritySettings settings, int actorId, CancellationToken token, string eventKind = "settings-updated", bool resetCounters = true)
    {
        await Read(token); await gate.WaitAsync(token);
        try
        {
            if (!settings.IsValid()) throw new ArgumentException("Invalid authentication security settings.");
            if (settings.Revision != document!.Settings.Revision) return false;
            var next = new AuthSecurityDocument(settings with { Revision = settings.Revision + 1 }, document.Audit.Append(new SecurityEvent(DateTimeOffset.UtcNow, eventKind, null, null, ActorId: actorId)).TakeLast(100).ToList());
            await using var connection = provider.CreateConnection(); await connection.OpenAsync(token);
            await using var transaction = await provider.BeginWrite(connection, token);
            await using (var seed = provider.Command(connection, transaction, provider.Choose(
                "INSERT INTO FeatureAccessPolicies (Id,Revision,Document) VALUES (2,0,@doc) ON DUPLICATE KEY UPDATE Id=2",
                "INSERT INTO FeatureAccessPolicies (Id,Revision,Document) VALUES (2,0,@doc) ON CONFLICT(Id) DO NOTHING"), ("@doc", JsonSerializer.Serialize(new AuthSecurityDocument(new(), []))))) await seed.ExecuteNonQueryAsync(token);
            await using var update = provider.Command(connection, transaction, "UPDATE FeatureAccessPolicies SET Revision=Revision+1,Document=@doc WHERE Id=2 AND Revision=@revision",
                ("@doc", JsonSerializer.Serialize(next)), ("@revision", settings.Revision));
            if (await update.ExecuteNonQueryAsync(token) != 1) return false;
            await transaction.CommitAsync(token);
            Volatile.Write(ref document, next); if (resetCounters) state.Reconfigure(settings); return true;
        }
        finally { gate.Release(); }
    }
}
