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
        [FromServices] IHostApplicationLifetime lifetime)
    {
        if (FeatureIdentity.AccountId(User) is not int account)
        {
            Response.StatusCode = 401;
            return;
        }
        if (jobs.Find(account, id) is not null)
        {
            Response.StatusCode = 409;
            return;
        }
        var job = jobs.Start(account, id);
        if (job is null)
        {
            Response.StatusCode = 429;
            Response.Headers.RetryAfter = "60";
            return;
        }

        OperationsAnalyticsMiddleware.SetModel(HttpContext, body.Model);
        using var work = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted,
            lifetime.ApplicationStopping, job.StopToken);
        work.CancelAfter(TimeSpan.FromMinutes(10));
        Response.StatusCode = 202;
        Response.Headers.CacheControl = "no-store";
        Response.ContentLength = 0;
        try { await Response.CompleteAsync(); }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }

        string? failure = null;
        try
        {
            var request = await ResolveRequest(body, account);
            await foreach (var chunk in _chat.StreamEvents(request, work.Token))
            {
                var frame = chunk.Attachment is null ? new ChatStreamEventResponse("delta", Text: chunk.Text ?? "")
                    : new ChatStreamEventResponse("attachment", Attachment: chunk.Attachment);
                if (!job.Append(frame))
                {
                    failure = "chat_output_size";
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (job.StopToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            OperationsAnalyticsMiddleware.MarkError(HttpContext);
            _logger.LogError("Chat generation failed. TraceId: {TraceId}; ExceptionType: {ExceptionType}",
                HttpContext.TraceIdentifier, error.GetType().Name);
            failure = error switch
            {
                ChatAttachmentException attachment => attachment.Code,
                KeyNotFoundException => "model_unavailable",
                NotSupportedException => "chat_unsupported",
                _ => "ai_unavailable"
            };
        }
        finally { job.Finish(failure); }
    }

    [HttpGet("chat/jobs/{id:guid}")]
    public async Task ReadJob(Guid id, [FromQuery] int offset, [FromServices] ChatJobs jobs)
    {
        if (offset < 0) { Response.StatusCode = 400; return; }
        var job = FeatureIdentity.AccountId(User) is int account ? jobs.Find(account, id) : null;
        if (job is null) { Response.StatusCode = 404; return; }

        var token = HttpContext.RequestAborted;
        Response.Headers.CacheControl = "no-store";
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            while (true)
            {
                var (frame, changed, completed) = job.At(offset);
                if (frame is not null)
                {
                    await ChatStreamWriter.Write(Response, frame.Type, token, frame.Text, frame.Code, frame.Attachment);
                    if (frame.Type is "completed" or "error") return;
                    offset++;
                }
                else if (completed) return;
                else
                {
                    try { await changed.WaitAsync(TimeSpan.FromSeconds(15), token); }
                    catch (TimeoutException)
                    {
                        await Response.WriteAsync(": keep-alive\n\n", token);
                        await Response.Body.FlushAsync(token);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    [HttpDelete("chat/jobs/{id:guid}")]
    public IActionResult StopJob(Guid id, [FromServices] ChatJobs jobs)
    {
        var job = FeatureIdentity.AccountId(User) is int account ? jobs.Find(account, id) : null;
        if (job is null) return NotFound();
        job.Stop();
        return NoContent();
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
