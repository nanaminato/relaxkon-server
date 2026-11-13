using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RelaxKon.Auth.Application.Analytics;
using RelaxKon.Auth.Application.Providers;
using RelaxKon.Auth.Application.Routing;
using RelaxKon.Auth.Contracts.Files;

namespace RelaxKon.Auth.Application.Chat;

public sealed class OpenAiResponsesHandler(IHttpClientFactory clients, IConfiguration configuration,
    IHttpContextAccessor requestContext, ILogger<OpenAiResponsesHandler> logger)
{
    private static readonly string[] DefaultToolModels =
        ["gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-6.1-sol", "gpt-6.1-terra"];

    private sealed record ContainerFile(string ContainerId, string FileId, string Name);

    public async IAsyncEnumerable<ChatOutput> Stream(ChatRequest request, ModelKey route,
        [EnumeratorCancellation] CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        token = timeout.Token;
        using var http = clients.CreateClient("AiProvider");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", route.SupplierKey!.ApiKey);
        var endpoint = OpenAiClientFactory.ResolveBaseUrl(route.SupplierKey.BaseUrl);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = route.Model,
            ["input"] = request.Messages.Select(MapMessage),
            ["stream"] = true,
            ["store"] = false
        };
        var imageModels = configuration.GetSection("OpenAiResponses:ImageGenerationModels").Get<string[]>() ?? DefaultToolModels;
        var fileModels = configuration.GetSection("OpenAiResponses:CodeInterpreterModels").Get<string[]>() ?? DefaultToolModels;
        var tools = new List<object>();
        if (imageModels.Contains(route.Model, StringComparer.Ordinal))
            tools.Add(new { type = "image_generation", output_format = "png" });
        if (fileModels.Contains(route.Model, StringComparer.Ordinal))
            tools.Add(new { type = "code_interpreter", container = new { type = "auto" } });
        if (tools.Count > 0) payload["tools"] = tools;

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/responses") { Content = JsonContent.Create(payload) };
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.BadRequest &&
            request.Messages.Any(input => input.Content.Parts.Any(part => part.Attachment is not null || part.ImageUrl is not null)))
        {
            try
            {
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (problem.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                    (ReadString(error, "param")?.StartsWith("input", StringComparison.Ordinal) == true ||
                     ReadString(error, "code") is "invalid_image" or "invalid_image_format" or "invalid_file" or "unsupported_file"))
                    throw new ChatAttachmentException("chat_attachment_rejected");
            }
            catch (JsonException) { /* Some configured gateways return a non-JSON error body. */ }
        }
        response.EnsureSuccessStatusCode();

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        var data = new List<string>();
        var seen = new HashSet<(string, string)>();
        var seenImages = new HashSet<string>(StringComparer.Ordinal);
        var imageCount = 0;
        long attachmentBytes = 0;
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Add(line[5..].TrimStart());
                continue;
            }
            if (line.Length != 0 || data.Count == 0) continue;
            using var frame = JsonDocument.Parse(string.Join('\n', data));
            data.Clear();
            var item = frame.RootElement;
            switch (ReadString(item, "type"))
            {
                case "response.output_text.delta":
                case "response.refusal.delta":
                    yield return new(Text: ReadString(item, "delta"));
                    break;
                case "response.output_item.done":
                    if (ReadImage(item.GetProperty("item")) is { } image && seenImages.Add(image))
                        yield return ImageOutput(image);
                    break;
                case "response.completed":
                    var completed = item.GetProperty("response");
                    RecordUsage(completed, route);
                    if (completed.TryGetProperty("output", out var outputs))
                        foreach (var output in outputs.EnumerateArray())
                            if (ReadImage(output) is { } finalImage && seenImages.Add(finalImage))
                                yield return ImageOutput(finalImage);
                    foreach (var file in ContainerFiles(completed))
                    {
                        if (!seen.Add((file.ContainerId, file.FileId))) continue;
                        var bytes = await ReadFile(http, endpoint, file, token);
                        attachmentBytes += bytes.Length;
                        CheckOutputSize(bytes.Length, attachmentBytes);
                        yield return new(Attachment: new(file.Name, FileType(file.Name), Convert.ToBase64String(bytes), bytes.Length));
                    }
                    yield break;
                case "response.failed":
                case "response.incomplete":
                case "error":
                    throw new InvalidOperationException("OpenAI Responses stream did not complete.");
            }
        }
        throw new EndOfStreamException("OpenAI Responses stream ended without a completion event.");

        ChatOutput ImageOutput(string image)
        {
            var size = Base64Size(image);
            attachmentBytes += size;
            CheckOutputSize(size, attachmentBytes);
            return new(Attachment: new($"image-{++imageCount}.png", "image/png", image, size));
        }
    }

    private static string? ReadImage(JsonElement output) =>
        ReadString(output, "type") == "image_generation_call" &&
        ReadString(output, "result") is { Length: > 0 } image ? image : null;

    private static object MapMessage(Message message) => new
    {
        role = message.Role,
        content = message.Content.Text is { } text
            ? new[] { TextPart(message.Role, text) }
            : message.Content.Parts.Select(part => part switch
            {
                { ImageUrl: { } image } => new { type = "input_image", image_url = image },
                { Attachment: { } file } => new { type = "input_file", filename = file.FileName,
                    file_data = $"data:application/pdf;base64,{file.FileContent}" },
                _ => TextPart(message.Role, part.Text ?? "")
            }).ToArray()
    };

    private static object TextPart(string role, string text) =>
        new { type = role == "assistant" ? "output_text" : "input_text", text };

    private static IEnumerable<ContainerFile> ContainerFiles(JsonElement response)
    {
        if (!response.TryGetProperty("output", out var outputs)) yield break;
        foreach (var output in outputs.EnumerateArray())
        {
            if (ReadString(output, "type") != "message" || !output.TryGetProperty("content", out var content)) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (!part.TryGetProperty("annotations", out var annotations)) continue;
                foreach (var annotation in annotations.EnumerateArray())
                {
                    if (ReadString(annotation, "type") == "container_file_citation" &&
                        ReadString(annotation, "container_id") is { Length: > 0 } container &&
                        ReadString(annotation, "file_id") is { Length: > 0 } file &&
                        ReadString(annotation, "filename") is { Length: > 0 } name)
                        yield return new(container, file, Path.GetFileName(name));
                }
            }
        }
    }

    private static async Task<byte[]> ReadFile(HttpClient http, string endpoint, ContainerFile file, CancellationToken token)
    {
        using var response = await http.GetAsync(
            $"{endpoint}/containers/{Uri.EscapeDataString(file.ContainerId)}/files/{Uri.EscapeDataString(file.FileId)}/content",
            HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > FileExtractionRequest.MaxFileBytes)
            throw new ChatAttachmentException("chat_output_size");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (await stream.ReadAsync(chunk, token) is var read && read != 0)
        {
            CheckOutputSize(buffer.Length + read, buffer.Length + read);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static long Base64Size(string value) => value.Length / 4L * 3 -
        (value.EndsWith("==", StringComparison.Ordinal) ? 2 : value.EndsWith('=') ? 1 : 0);

    private static void CheckOutputSize(long size, long total)
    {
        if (size <= 0 || size > FileExtractionRequest.MaxFileBytes || total > 2 * FileExtractionRequest.MaxFileBytes)
            throw new ChatAttachmentException("chat_output_size");
    }

    private static string FileType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".csv" => "text/csv",
        ".txt" => "text/plain",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };

    private static string? ReadString(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private void RecordUsage(JsonElement response, ModelKey route)
    {
        if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return;
        var input = usage.GetProperty("input_tokens").GetInt32();
        var output = usage.GetProperty("output_tokens").GetInt32();
        OperationsAnalyticsMiddleware.SetUsage(requestContext.HttpContext, input, output);
        int? cached = usage.TryGetProperty("input_tokens_details", out var details) &&
            details.ValueKind == JsonValueKind.Object && details.TryGetProperty("cached_tokens", out var count) &&
            count.TryGetInt32(out var tokens) ? tokens : null;
        logger.LogInformation("AI chat usage. RouteId: {RouteId}; SupplierKeyId: {SupplierKeyId}; LogicalModel: {LogicalModel}; ProviderModel: {ProviderModel}; InputTokens: {InputTokens}; OutputTokens: {OutputTokens}; CachedInputTokens: {CachedInputTokens}",
            route.RouteId, route.SupplierKey?.SupplierKeyId, route.LogicalModel, route.Model, input, output, cached);
    }
}
