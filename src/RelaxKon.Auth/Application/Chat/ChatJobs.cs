using RelaxKon.Auth.Contracts.Chat;

namespace RelaxKon.Auth.Application.Chat;

/// <summary>Bounded replay for browser connections to a single server instance.</summary>
public sealed class ChatJobs(TimeProvider clock)
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private long reservedBytes;
    private const int BufferBudget = 96 * 1024 * 1024;
    private readonly Dictionary<(int Account, Guid Id), ChatJob> jobs = [];

    internal ChatJob? Find(int account, Guid id)
    {
        lock (jobs)
        {
            Prune();
            return jobs.GetValueOrDefault((account, id));
        }
    }

    internal ChatJob? Start(int account, Guid id)
    {
        lock (jobs)
        {
            Prune();
            if (jobs.ContainsKey((account, id)) ||
                jobs.Count(item => item.Key.Account == account && item.Value.CompletedAt is null) >= 32) return null;
            while (jobs.Count >= 128 || Interlocked.Read(ref reservedBytes) > BufferBudget / 2)
            {
                var oldest = jobs.Where(pair => pair.Value.CompletedAt is not null)
                    .OrderBy(pair => pair.Value.CompletedAt).FirstOrDefault();
                if (oldest.Value is null) break;
                Remove(oldest.Key);
            }
            if (jobs.Count >= 128) return null;
            var job = new ChatJob(clock, Reserve);
            return jobs.TryAdd((account, id), job) ? job : null;
        }
    }

    private void Prune()
    {
        var cutoff = clock.GetUtcNow() - Retention;
        foreach (var key in jobs.Where(pair => pair.Value.CompletedAt < cutoff).Select(pair => pair.Key).ToArray())
            Remove(key);
    }
    private bool Reserve(int bytes)
    {
        if (Interlocked.Add(ref reservedBytes, bytes) <= BufferBudget) return true;
        Interlocked.Add(ref reservedBytes, -bytes);
        return false;
    }

    private void Remove((int Account, Guid Id) key)
    {
        if (jobs.Remove(key, out var removed)) Interlocked.Add(ref reservedBytes, -removed.BufferedBytes);
    }
}

internal sealed class ChatJob(TimeProvider clock, Func<int, bool> reserve)
{
    private readonly List<ChatStreamEventResponse> events = [];
    private readonly CancellationTokenSource stop = new();
    private TaskCompletionSource changed = NewSignal();
    private int bufferedBytes;
    public int BufferedBytes { get { lock (events) return bufferedBytes; } }
    private DateTimeOffset? completedAt;

    public CancellationToken StopToken => stop.Token;
    public DateTimeOffset? CompletedAt { get { lock (events) return completedAt; } }
    public void Stop() => stop.Cancel();

    public bool Append(ChatStreamEventResponse frame)
    {
        lock (events)
        {
            // Bound both replay memory and event overhead, including generated attachments.
            var characters = (frame.Text?.Length ?? 0) + (frame.Attachment?.FileContent.Length ?? 0);
            var bytes = 128 + characters * sizeof(char);
            if (bufferedBytes + bytes > 32 * 1024 * 1024 || events.Count >= 32000 || !reserve(bytes)) return false;
            bufferedBytes += bytes;
            Add(frame);
            return true;
        }
    }

    public void Finish(string? error = null)
    {
        lock (events)
        {
            completedAt = clock.GetUtcNow();
            Add(error is null ? new("completed") : new("error", Code: error));
        }
    }

    public (ChatStreamEventResponse? Frame, Task Changed, bool Completed) At(int index)
    {
        lock (events)
            return (index < events.Count ? events[index] : null, changed.Task, completedAt is not null);
    }

    private void Add(ChatStreamEventResponse frame)
    {
        events.Add(frame);
        changed.TrySetResult();
        changed = NewSignal();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
