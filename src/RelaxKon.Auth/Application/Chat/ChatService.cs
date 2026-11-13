using System.Runtime.CompilerServices;
using RelaxKon.Auth.Application.Providers;
using RelaxKon.Auth.Application.Routing;

namespace RelaxKon.Auth.Application.Chat;

public interface IChatService
{
    IAsyncEnumerable<string> Stream(ChatRequest request, CancellationToken cancellationToken);

    async IAsyncEnumerable<ChatOutput> StreamEvents(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var text in Stream(request, cancellationToken)) yield return new(Text: text);
    }
}

public sealed class ChatService(IChatHandlerObtain handlers, QuickModel routes, IRouteHealthTracker health,
    PlayMixModelBacker backer, ILogger<ChatService> logger, IChatRouteAffinityStore? affinity = null, ChatAttachmentInput? attachments = null,
    OpenAiResponsesHandler? responses = null) : IChatService
{
    public async IAsyncEnumerable<string> Stream(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var output in StreamEvents(request, cancellationToken))
            if (output.Text is not null) yield return output.Text;
    }

    public async IAsyncEnumerable<ChatOutput> StreamEvents(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prepared = new Dictionary<bool, ChatRequest>();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        double? firstTokenMilliseconds = null;
        var failed = new HashSet<int>();
        var hasAffinity = affinity is not null && request.AccountId is > 0 &&
            !string.IsNullOrWhiteSpace(request.ConversationId) && request.ConversationId.Length <= 128;
        var binding = hasAffinity ? await affinity!.Read(request.AccountId!.Value,
            request.ConversationId!, request.Model!, cancellationToken) : null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModelKey route;
            try { route = routes.GetModelAndKey(request.Model!, failed, binding); }
            catch (KeyNotFoundException exception) when (failed.Count > 0)
            { throw new InvalidOperationException("All candidate AI routes failed.", exception); }
            var identity = RouteAffinity.From(route);
            route.Model = backer.Backer(route.Model!);
            var useResponses = responses is not null && route.SupplierKey?.RequestIdentifier == (int)HandlerType.Openai;
            if (!prepared.TryGetValue(useResponses, out var routedRequest))
            {
                routedRequest = attachments is null ? request : await attachments.Prepare(request, useResponses, cancellationToken);
                prepared.Add(useResponses, routedRequest);
            }
            var response = useResponses
                ? responses!.Stream(routedRequest, route, cancellationToken)
                : StreamRoute(routedRequest, route, cancellationToken);
            await using var stream = response.GetAsyncEnumerator(cancellationToken);
            var emitted = false;
            var retry = false;
            while (true)
            {
                bool hasNext;
                try { hasNext = await stream.MoveNextAsync(); }
                catch (OperationCanceledException) { throw; }
                catch (ChatAttachmentException) { throw; }
                catch (Exception exception)
                {
                    health.RecordFailure(route.RouteId);
                    if (emitted) throw;
                    failed.Add(route.RouteId);
                    logger.LogWarning("AI route failed before output. RouteId: {RouteId}; ExceptionType: {ExceptionType}",
                        route.RouteId, exception.GetType().Name);
                    retry = true;
                    break;
                }
                if (!hasNext) break;
                if (stream.Current.Attachment is null && string.IsNullOrEmpty(stream.Current.Text)) continue;
                firstTokenMilliseconds ??= timer.Elapsed.TotalMilliseconds;
                emitted = true;
                yield return stream.Current;
            }
            if (retry) continue;
            health.RecordSuccess(route.RouteId);
            if (hasAffinity) await affinity!.Write(request.AccountId!.Value, request.ConversationId!,
                request.Model!, identity, cancellationToken);
            logger.LogInformation("AI chat routing completed. RouteId: {RouteId}; LogicalModel: {LogicalModel}; AffinityReused: {AffinityReused}; FailoverCount: {FailoverCount}; FirstTokenMilliseconds: {FirstTokenMilliseconds}; DurationMilliseconds: {DurationMilliseconds}",
                route.RouteId, request.Model, binding == identity, failed.Count,
                firstTokenMilliseconds, timer.Elapsed.TotalMilliseconds);
            yield break;
        }
    }
    private async IAsyncEnumerable<ChatOutput> StreamRoute(ChatRequest request, ModelKey route,
        [EnumeratorCancellation] CancellationToken token)
    {
        var handler = handlers.GetHandler((HandlerType)route.SupplierKey!.RequestIdentifier);
        await foreach (var text in handler.Stream(request, route, token)) yield return new(Text: text);
    }

}
