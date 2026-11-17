using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using RelaxKon.Auth.Application.Providers;
using RelaxKon.Auth.Security.FeatureAccess;

namespace RelaxKon.Auth.Security.ChildKeys;

[ApiController]
[Authorize(AuthenticationSchemes = ChildKeyAuthentication.SchemeName)]
[Route("api/native")]
public sealed class NativeProviderController(IHttpClientFactory clients, ILogger<NativeProviderController> logger) : ControllerBase
{
    public const string ClientName = "NativeProvider";

    private static readonly string[] ForwardedRequestHeaders =
    [
        "Accept", "Content-Encoding", "Idempotency-Key", "anthropic-version", "anthropic-beta",
        "OpenAI-Beta", "X-DashScope-Async", "X-DashScope-SSE", "X-APIMart-Response-Version"
    ];

    private static readonly HashSet<string> ExcludedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "Proxy-Connection",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Set-Cookie", "Location", "Alt-Svc",
        "Refresh", "Server", "WWW-Authenticate", "Clear-Site-Data", "Content-Security-Policy"
    };

    [AcceptVerbs("GET", "POST", Route = "{**path}")]
    [FeatureAccess("chat")]
    public async Task Forward(string? path)
    {
        var parent = ChildKeyAuthentication.Parent(HttpContext);
        var provider = (HandlerType)parent.RequestIdentifier;
        if (path is null || !NativeProvider.Allows(provider, Request.Method, path))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Preserve the original query encoding. Key query fields belong to the proxy boundary.
        var query = Request.Query.Keys.Any(name => name.Equals("key", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("api-key", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("access_token", StringComparison.OrdinalIgnoreCase))
            ? null : Request.QueryString.Value ?? "";
        var target = query is null ? null : NativeProvider.Target(NativeProvider.BaseAddress(parent)!, path, query);
        if (target is null)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var message = new HttpRequestMessage(new HttpMethod(Request.Method), target);
        if (Request.Method == "POST" && HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        {
            message.Content = new StreamContent(Request.Body);
            message.Content.Headers.ContentLength = Request.ContentLength;
            if (Request.ContentType is not null)
                message.Content.Headers.TryAddWithoutValidation("Content-Type", Request.ContentType);
        }
        foreach (var header in ForwardedRequestHeaders)
        {
            if (!Request.Headers.TryGetValue(header, out var value)) continue;
            if (!message.Headers.TryAddWithoutValidation(header, value.ToArray()))
                message.Content?.Headers.TryAddWithoutValidation(header, value.ToArray());
        }

        switch (provider)
        {
            case HandlerType.Anthropic:
                message.Headers.Add("x-api-key", parent.ApiKey);
                break;
            case HandlerType.Google:
                message.Headers.Add("x-goog-api-key", parent.ApiKey);
                break;
            default:
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", parent.ApiKey);
                break;
        }

        using var client = clients.CreateClient(ClientName);
        try
        {
            using var upstream = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Response.StatusCode = (int)upstream.StatusCode;
            var connectionHeaders = upstream.Headers.Connection.SelectMany(value => value.Split(','))
                .Select(value => value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var header in upstream.Headers.Concat(upstream.Content.Headers))
            {
                if (!ExcludedResponseHeaders.Contains(header.Key) && !connectionHeaders.Contains(header.Key) &&
                    !header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.StartsWith("X-Accel-", StringComparison.OrdinalIgnoreCase))
                    Response.Headers[header.Key] = header.Value.ToArray();
            }
            Response.Headers.CacheControl = "no-store";
            Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
            Response.Headers.XContentTypeOptions = "nosniff";
            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            if (upstream.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                Response.Headers["X-Accel-Buffering"] = "no";
                await Response.StartAsync(timeout.Token);
                await using var stream = await upstream.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    await Response.Body.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    await Response.Body.FlushAsync(timeout.Token);
                }
            }
            else
            {
                await upstream.Content.CopyToAsync(Response.Body, timeout.Token);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            if (Response.HasStarted || HttpContext.RequestAborted.IsCancellationRequested)
            {
                HttpContext.Abort();
                return;
            }
            logger.LogWarning("Native provider request failed. Provider: {Provider}; Error: {Error}; Upstream status: {UpstreamStatus}",
                provider, exception is HttpRequestException network ? network.HttpRequestError.ToString() : exception.GetType().Name,
                (exception as HttpRequestException)?.StatusCode);
            Response.Clear();
            Response.StatusCode = timeout.IsCancellationRequested ? 504 : 502;
        }
    }
}
