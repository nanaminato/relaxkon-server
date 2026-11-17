using RelaxKon.Auth.Contracts.Chat;
using RelaxKon.Auth.Security.FeatureAccess;

namespace RelaxKon.Auth.Application.Chat;

public sealed class ChatJobRunner(ChatJobs jobs, IChatService chat, ILogger<ChatJobRunner> logger)
{
    public async Task Run(int account, string id, ChatRequest request, CancellationToken token,
        Func<CancellationToken, Task<bool>>? renewQuota = null)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        work.CancelAfter(TimeSpan.FromMinutes(10));
        var heartbeat = KeepAlive(account, id, renewQuota, work);
        string? failure = null;
        var offset = 0;
        try
        {
            await foreach (var chunk in chat.StreamEvents(request, work.Token))
            {
                ChatStreamEventResponse frame = chunk.Attachment is { } attachment
                    ? new("attachment", Attachment: attachment)
                    : new("delta", Text: chunk.Text ?? "");
                var next = await jobs.Append(account, id, offset, [frame], work.Token);
                if (next < 0)
                {
                    failure = "chat_output_size";
                    break;
                }
                offset = next;
            }
            work.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { failure = ChatJobs.Interrupted; }
        catch (Exception error)
        {
            logger.LogWarning("Chat job failed. Job: {JobId}; ExceptionType: {ExceptionType}", id, error.GetType().Name);
            failure = error switch
            {
                ChatAttachmentException attachment => attachment.Code,
                KeyNotFoundException => "model_unavailable",
                NotSupportedException => "chat_unsupported",
                _ => "ai_unavailable"
            };
        }
        finally
        {
            await work.CancelAsync();
            await heartbeat;
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await jobs.Finish(account, id, failure, finish.Token); }
            catch (Exception error)
            {
                logger.LogWarning("Chat job finalization deferred to lease expiry. Job: {JobId}; ExceptionType: {ExceptionType}",
                    id, error.GetType().Name);
            }
        }
    }

    private async Task KeepAlive(int account, string id, Func<CancellationToken, Task<bool>>? renewQuota,
        CancellationTokenSource work)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(work.Token))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                if (await jobs.Renew(account, id, timeout.Token) &&
                    (renewQuota is null || await renewQuota(timeout.Token))) continue;
                await work.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogWarning("Chat job lease could not be renewed. Job: {JobId}; ExceptionType: {ExceptionType}",
                id, error.GetType().Name);
            await work.CancelAsync();
        }
    }
}

public sealed class ChatJobRecovery(ChatJobs jobs, IServiceScopeFactory scopes, ILogger<ChatJobRecovery> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bound parallel recovery work and give ordinary HTTP admissions priority.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        var running = new Dictionary<(int Account, string Id), Task>();
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var key in running.Where(entry => entry.Value.IsCompleted).Select(entry => entry.Key).ToArray())
                    running.Remove(key);
                try
                {
                    var queued = await jobs.Recoverable(stoppingToken);
                    foreach (var job in queued)
                    {
                        if (running.Count >= 4) break;
                        var key = (job.AccountId, job.Id);
                        if (!running.ContainsKey(key)) running.Add(key, Recover(job, stoppingToken));
                    }
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Chat job recovery scan failed. ExceptionType: {ExceptionType}", error.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(running.Values); }
    }

    private async Task Recover(ChatGeneration job, CancellationToken token)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var store = services.GetRequiredService<FeatureAccessStore>();
            var roles = await services.GetRequiredService<FeatureIdentity>().Roles(job.AccountId, token);
            var policy = FeaturePolicies.Resolve(await store.Read(token), "chat", roles);
            if (!policy.Enabled)
            {
                await jobs.RejectQueued(job.AccountId, job.Id, "feature_disabled", token);
                return;
            }

            // The original POST already charged this job. Recovery acquires concurrency only.
            var admission = await store.AcquireRecoveredChat(job.AccountId, policy, job.SourceFingerprint, token);
            if (admission.LeaseId is not { } lease) return;
            try
            {
                var request = await jobs.Claim(job.AccountId, job.Id, token);
                if (request is not null)
                    await services.GetRequiredService<ChatJobRunner>().Run(job.AccountId, job.Id, request, token,
                        renew => store.Renew(lease, renew));
            }
            finally
            {
                using var release = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await store.Release(lease, release.Token);
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            logger.LogWarning("Could not recover queued chat job. Job: {JobId}; ExceptionType: {ExceptionType}",
                job.Id, error.GetType().Name);
        }
        catch (Exception) when (token.IsCancellationRequested) { }
    }
}
