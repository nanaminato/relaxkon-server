using RelaxKon.Auth.Security.FeatureAccess;
using RelaxKon.Auth.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RelaxKon.Auth.Contracts.Chat;
using RelaxKon.Auth.Application.Chat;
using RelaxKon.Auth.Application.Analytics;
using RelaxKon.Auth.Application.Providers;
using RelaxKon.Auth.Application.Routing;
using RelaxKon.Auth.Models.AI.Chat;
using RelaxKon.Auth.Repositories.Catalog;

namespace RelaxKon.Auth.Controllers.AI.Chat;

[Authorize]
[ApiController]
[Route("api/ai")]
public class ChatController : Controller
{
    private readonly IChatService _chat;
    private readonly QuickModel _quickModel;
    private readonly IModelRepository _modelRepository;
    private readonly ILogger<ChatController> _logger;
    public ChatController(IChatService chat, QuickModel quickModel,
        IModelRepository modelRepository, ILogger<ChatController> logger)
    {
        _chat = chat;
        _quickModel = quickModel;
        _modelRepository = modelRepository;
        _logger = logger;
    }

    [FeatureAccess("chat")]
    [HttpPost("chat")]
    public async Task Chat(ChatCompletionRequest chatBody)
    {
        OperationsAnalyticsMiddleware.SetModel(HttpContext, chatBody.Model);
        var started = false;
        var framed = Request.Headers.Accept.ToString().Split(',').Any(value => value.Trim().StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase));
        try
        {
            if (string.IsNullOrWhiteSpace(chatBody.Model))
            {
                await WriteProblem(400, ApiMessages.Get("model_required"));
                return;
            }
            var request = await ResolveRequest(chatBody, FeatureIdentity.AccountId(User));

            await foreach (var chunk in _chat.StreamEvents(request, HttpContext.RequestAborted))
            {
                if (chunk.Attachment is not null)
                {
                    if (framed)
                    {
                        started = true;
                        await ChatStreamWriter.Write(Response, "attachment", HttpContext.RequestAborted, attachment: chunk.Attachment);
                    }
                    continue;
                }

                started = true;
                if (framed) await ChatStreamWriter.Write(Response, "delta", HttpContext.RequestAborted, text: chunk.Text);
                else
                {
                    if (!Response.HasStarted) Response.ContentType = "text/plain; charset=utf-8";
                    await Response.WriteAsync(chunk.Text ?? "", HttpContext.RequestAborted);
                    await Response.Body.FlushAsync(HttpContext.RequestAborted);
                }
            }
            if (framed) await ChatStreamWriter.Write(Response, "completed", HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            HttpContext.Abort();
        }
        catch (Exception exception)
        {
            if (started || Response.HasStarted) OperationsAnalyticsMiddleware.MarkError(HttpContext);
            _logger.LogError("AI chat failed. TraceId: {TraceId}; ExceptionType: {ExceptionType}",
                HttpContext.TraceIdentifier, exception.GetType().Name);
            if (started || Response.HasStarted)
            {
                if (framed) await ChatStreamWriter.Write(Response, "error", HttpContext.RequestAborted, code: exception is ChatAttachmentException attachmentError ? attachmentError.Code : "ai_unavailable");
                else HttpContext.Abort();
                return;
            }
            var (status, code) = exception switch
            {
                ChatAttachmentException error => (error.Status, error.Code),
                KeyNotFoundException => (400, "model_unavailable"),
                NotSupportedException => (400, "chat_unsupported"),
                _ => (502, "ai_unavailable")
            };
            if (exception is ChatAttachmentException { RetryAfter: int seconds })
                Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await WriteProblem(status, ApiMessages.Get(code), code);
        }
    }

    [FeatureAccess("chat", ContinueAfterDisconnect = true)]
    [HttpPost("chat/jobs/{id:guid}")]
    public async Task StartJob(Guid id, ChatCompletionRequest body, [FromServices] ChatJobs jobs,
        [FromServices] ChatJobRunner runner, [FromServices] IHostApplicationLifetime lifetime)
    {
        if (FeatureIdentity.AccountId(User) is not int account)
        {
            Response.StatusCode = 401;
            return;
        }

        var jobId = id.ToString("N");
        var request = await ResolveRequest(body, account);
        if (!await jobs.Enqueue(account, jobId, request, FeatureAccessStore.FingerprintSource(HttpContext.Connection.RemoteIpAddress),
            body.AssistantMessageId, HttpContext.RequestAborted))
        {
            Response.StatusCode = 409;
            return;
        }

        OperationsAnalyticsMiddleware.SetModel(HttpContext, body.Model);
        Response.StatusCode = 202;
        Response.Headers.CacheControl = "no-store";
        Response.ContentLength = 0;
        try { await Response.CompleteAsync(); }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }

        using var work = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, lifetime.ApplicationStopping);
        // A shutdown before the claim leaves the original request recoverable.
        if (!work.IsCancellationRequested && await jobs.Claim(account, jobId, work.Token) is { } claimed)
            await runner.Run(account, jobId, claimed, work.Token);
    }

    [HttpGet("chat/jobs")]
    public async Task<IActionResult> DiscoverJobs(long historyId, [FromServices] ChatJobs jobs, CancellationToken token)
    {
        if (historyId is < 1 or > 9007199254740991) return BadRequest();
        if (FeatureIdentity.AccountId(User) is not int account) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        return Ok(await jobs.ForHistory(account, historyId, token));
    }

    [HttpGet("chat/jobs/{id:guid}")]
    public async Task ReadJob(Guid id, [FromQuery] int offset, [FromServices] ChatJobs jobs)
    {
        if (offset < 0) { Response.StatusCode = 400; return; }
        if (FeatureIdentity.AccountId(User) is not int account) { Response.StatusCode = 404; return; }

        var token = HttpContext.RequestAborted;
        var jobId = id.ToString("N");
        var heartbeat = TimeProvider.System.GetTimestamp();
        try
        {
            var page = await jobs.Read(account, jobId, offset, token);
            if (page is null)
            {
                Response.StatusCode = 404;
                await Response.WriteAsJsonAsync(new { code = "chat_job_missing" }, token);
                return;
            }
            if (offset > page.Count) { Response.StatusCode = 400; return; }

            Response.Headers.CacheControl = "no-store";
            Response.ContentType = "text/event-stream; charset=utf-8";
            Response.Headers["X-Accel-Buffering"] = "no";
            await Response.StartAsync(token);
            while (true)
            {
                foreach (var frame in page.Frames)
                {
                    await ChatStreamWriter.Write(Response, frame.Type, token, frame.Text, frame.Code, frame.Attachment);
                    offset++;
                }
                if (page.Finished && offset >= page.Count)
                {
                    await ChatStreamWriter.Write(Response, page.ErrorCode is null ? "completed" : "error", token,
                        code: page.ErrorCode);
                    return;
                }
                if (page.Frames.Length == 0)
                {
                    if (TimeProvider.System.GetElapsedTime(heartbeat) >= TimeSpan.FromSeconds(15))
                    {
                        await Response.WriteAsync(": keep-alive\n\n", token);
                        await Response.Body.FlushAsync(token);
                        heartbeat = TimeProvider.System.GetTimestamp();
                    }
                    await Task.Delay(250, token);
                }
                page = await jobs.Read(account, jobId, offset, token);
                if (page is null)
                {
                    await ChatStreamWriter.Write(Response, "error", token, code: "chat_job_missing");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    [HttpDelete("chat/jobs/{id:guid}")]
    public async Task<IActionResult> StopJob(Guid id, [FromServices] ChatJobs jobs, CancellationToken token)
    {
        return FeatureIdentity.AccountId(User) is int account && await jobs.Stop(account, id.ToString("N"), token)
            ? NoContent() : NotFound();
    }


    private async Task<ChatRequest> ResolveRequest(ChatCompletionRequest body, int? account)
    {
        var request = body.ToApplication(account);
        if (request.Messages.Any(message => message.Content.Parts.Any(part =>
            part.ImageUrl is not null || string.Equals(Path.GetExtension(part.Attachment?.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))))
        {
            var models = await _modelRepository.GetChatModelsAsync();
            request.Vision = models?.Any(model => model.ModelValue == request.Model && model.Vision) == true;
        }
        return request;
    }

    private async Task WriteProblem(int statusCode, string title, string? code = null)
    {
        if (Response.HasStarted)
        {
            return;
        }

        Response.StatusCode = statusCode;
        Response.ContentType = "application/problem+json";
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Instance = HttpContext.Request.Path
        };
        if (code is not null) problem.Extensions["code"] = code;
        await Response.WriteAsJsonAsync(problem, contentType: "application/problem+json", options: null);
        await Response.CompleteAsync();
    }

    [HttpGet("models")]
    public async Task<ActionResult<List<ChatModelResponse>>> GetChatModels()
    {
        var models = await _modelRepository.GetChatModelsAsync();
        return Ok((models ?? [])
            .Where(model => _quickModel.IsModelAvailable(model.ModelValue))
            .Select(m => new ChatModelResponse(m.Name, m.ModelValue, m.Vision)).ToList());
    }
}
