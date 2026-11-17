using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RelaxKon.Auth.Contracts.Chat;
using RelaxKon.Auth.Data.Contexts;

namespace RelaxKon.Auth.Application.Chat;

public sealed class ChatGeneration
{
    public int AccountId { get; set; }
    public string Id { get; set; } = "";
    public string State { get; set; } = ChatJobs.Queued;
    public string? RequestJson { get; set; }
    public string? SourceFingerprint { get; set; }
    public string? ErrorCode { get; set; }
    public long CreatedAt { get; set; }
    public long LeaseUntil { get; set; }
    public long? HistoryId { get; set; }
    public long? MessageId { get; set; }
    public string? Model { get; set; }
    public int FrameCount { get; set; }
    public int OutputBytes { get; set; }
}

public sealed class ChatGenerationFrame
{
    public int AccountId { get; set; }
    public string JobId { get; set; } = "";
    public int Sequence { get; set; }
    public string Payload { get; set; } = "";
}

public sealed class ChatJobs(IServiceScopeFactory scopes, TimeProvider clock)
{
    internal const string Queued = "queued";
    internal const string Running = "running";
    internal const string Completed = "completed";
    internal const string Failed = "failed";
    internal const string Stopped = "stopped";
    internal const string Interrupted = "chat_generation_interrupted";
    private const int LeaseSeconds = 30;
    private const int MaxFrames = 32000;
    private const int MaxBytes = 32 * 1024 * 1024;
    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    internal Task<bool> Enqueue(int account, string id, ChatRequest request, string? sourceFingerprint, long? messageId, CancellationToken token) =>
        Use(async db =>
        {
            if (await Query(db, account, id).AnyAsync(token)) return false;
            var historyId = long.TryParse(request.ConversationId, out var parsed) && parsed is > 0 and <= 9007199254740991
                ? (long?)parsed : null;
            var job = new ChatGeneration
            {
                AccountId = account, Id = id, RequestJson = JsonSerializer.Serialize(request),
                HistoryId = historyId, MessageId = historyId.HasValue ? messageId : null, Model = request.Model,
                SourceFingerprint = sourceFingerprint, CreatedAt = Now, LeaseUntil = Now + LeaseSeconds
            };
            db.Add(job);
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateException)
            {
                if (await Query(db, account, id).AnyAsync(token)) return false;
                throw;
            }
            return true;
        });

    internal Task<List<ChatGeneration>> Recoverable(CancellationToken token) => Use(async db =>
    {
        var now = Now;
        await db.Set<ChatGeneration>().Where(job => job.LeaseUntil <= now && job.State == Running)
            .ExecuteUpdateAsync(set => set.SetProperty(job => job.State, Failed)
                .SetProperty(job => job.ErrorCode, Interrupted)
                .SetProperty(job => job.RequestJson, (string?)null)
                .SetProperty(job => job.SourceFingerprint, (string?)null), token);
        return await db.Set<ChatGeneration>().AsNoTracking()
            .Where(job => job.State == Queued && job.LeaseUntil <= now)
            .OrderBy(job => job.CreatedAt).Take(16)
            .Select(job => new ChatGeneration { AccountId = job.AccountId, Id = job.Id, SourceFingerprint = job.SourceFingerprint })
            .ToListAsync(token);
    });

    internal Task<ChatRequest?> Claim(int account, string id, CancellationToken token) => Use(async db =>
    {
        var query = Query(db, account, id).Where(job => job.State == Queued);
        var payload = await query.Select(job => job.RequestJson).SingleOrDefaultAsync(token);
        if (payload is null) return null;
        var request = JsonSerializer.Deserialize<ChatRequest>(payload)!;
        var now = Now;
        var changed = await query.ExecuteUpdateAsync(set => set.SetProperty(job => job.State, Running)
            .SetProperty(job => job.LeaseUntil, now + LeaseSeconds)
            .SetProperty(job => job.RequestJson, (string?)null)
            .SetProperty(job => job.SourceFingerprint, (string?)null), token);
        return changed == 1 ? request : null;
    });

    internal Task<bool> Renew(int account, string id, CancellationToken token) => Use(async db =>
    {
        var now = Now;
        return await Active(db, account, id, now).ExecuteUpdateAsync(set =>
            set.SetProperty(job => job.LeaseUntil, now + LeaseSeconds), token) == 1;
    });

    internal Task<int> Append(int account, string id, int offset, IReadOnlyList<ChatStreamEventResponse> frames,
        CancellationToken token) => Use(async db =>
    {
        var payloads = frames.Select(frame => JsonSerializer.Serialize(frame)).ToArray();
        var bytes = payloads.Sum(payload => (long)System.Text.Encoding.UTF8.GetByteCount(payload));
        if (offset + frames.Count > MaxFrames || bytes > MaxBytes) return -1;
        var size = (int)bytes;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var now = Now;
        var changed = await Active(db, account, id, now).Where(job => job.FrameCount == offset && job.OutputBytes <= MaxBytes - size)
            .ExecuteUpdateAsync(set => set.SetProperty(job => job.FrameCount, job => job.FrameCount + frames.Count)
                .SetProperty(job => job.OutputBytes, job => job.OutputBytes + size), token);
        if (changed == 0) return -1;
        db.AddRange(payloads.Select((payload, index) => new ChatGenerationFrame
        {
            AccountId = account, JobId = id, Sequence = offset + index, Payload = payload
        }));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return offset + frames.Count;
    });

    internal Task Finish(int account, string id, string? error, CancellationToken token) => Use(async db =>
        await Query(db, account, id).Where(job => job.State == Running)
            .ExecuteUpdateAsync(set => set.SetProperty(job => job.State, error == null ? Completed : Failed)
                .SetProperty(job => job.ErrorCode, error), token));

    internal Task RejectQueued(int account, string id, string error, CancellationToken token) => Use(async db =>
        await Query(db, account, id).Where(job => job.State == Queued)
            .ExecuteUpdateAsync(set => set.SetProperty(job => job.State, Failed)
                .SetProperty(job => job.ErrorCode, error)
                .SetProperty(job => job.RequestJson, (string?)null)
                .SetProperty(job => job.SourceFingerprint, (string?)null), token));

    internal Task<bool> Stop(int account, string id, CancellationToken token) => Use(async db =>
    {
        var query = Query(db, account, id);
        var changed = await query.Where(job => job.State == Running || job.State == Queued)
            .ExecuteUpdateAsync(set => set.SetProperty(job => job.State, Stopped)
                .SetProperty(job => job.RequestJson, (string?)null)
                .SetProperty(job => job.SourceFingerprint, (string?)null), token);
        return changed == 1 || await query.AnyAsync(token);
    });

    internal Task<ChatJobPage?> Read(int account, string id, int offset, CancellationToken token) => Use<ChatJobPage?>(async db =>
    {
        var job = await Query(db, account, id).Select(job => new { job.State, job.ErrorCode, job.FrameCount }).SingleOrDefaultAsync(token);
        if (job is null) return null;
        if (offset >= job.FrameCount) return new(job.State, job.ErrorCode, job.FrameCount, []);
        // Bound the amount of JSON held by each reader when a reply includes large files.
        var payloads = await db.Set<ChatGenerationFrame>().Where(frame =>
                frame.AccountId == account && frame.JobId == id && frame.Sequence >= offset && frame.Sequence < job.FrameCount)
            .OrderBy(frame => frame.Sequence).Take(8).Select(frame => frame.Payload).ToArrayAsync(token);
        return new(job.State, job.ErrorCode, job.FrameCount,
            payloads.Select(payload => JsonSerializer.Deserialize<ChatStreamEventResponse>(payload)!).ToArray());
    });

    internal Task<ChatJobReceipt[]> ForHistory(int account, long historyId, CancellationToken token) => Use(async db =>
    {
        var records = await db.Set<ChatGeneration>().AsNoTracking()
            .Where(job => job.AccountId == account && job.HistoryId == historyId && job.MessageId != null)
            .OrderByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id).Take(64)
            .Select(job => new ChatJobReceipt(job.Id, job.MessageId!.Value, job.Model)).ToArrayAsync(token);
        return records.DistinctBy(job => job.MessageId)
            .Select(job => job with { Id = Guid.ParseExact(job.Id, "N").ToString() }).ToArray();
    });

    private async Task<T> Use<T>(Func<AuthContext, Task<T>> operation)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<AuthContext>());
    }

    private static IQueryable<ChatGeneration> Query(AuthContext db, int account, string id) =>
        db.Set<ChatGeneration>().Where(job => job.AccountId == account && job.Id == id);

    private static IQueryable<ChatGeneration> Active(AuthContext db, int account, string id, long now) =>
        Query(db, account, id).Where(job => job.State == Running && job.LeaseUntil > now);
}

internal sealed record ChatJobPage(string State, string? ErrorCode, int Count, ChatStreamEventResponse[] Frames)
{
    public bool Finished => State is ChatJobs.Completed or ChatJobs.Failed or ChatJobs.Stopped;
}

internal sealed record ChatJobReceipt(string Id, long MessageId, string? Model);
