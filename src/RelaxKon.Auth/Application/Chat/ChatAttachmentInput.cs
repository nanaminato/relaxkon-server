using RelaxKon.Auth.Security.FeatureAccess;
using RelaxKon.Auth.Contracts.Files;
using RelaxKon.Auth.Controllers.Files;

namespace RelaxKon.Auth.Application.Chat;

public sealed class ChatAttachmentInput(IFileContentExtractor extractor, FeatureIdentity? identity = null,
    FeatureAccessStore? store = null, ILogger<ChatAttachmentInput>? logger = null)
{
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    { "image/png", "image/jpeg", "image/webp", "image/gif" };

    public async Task<ChatRequest> Prepare(ChatRequest request, bool useResponses, CancellationToken token)
    {
        if (request.AccountId is not int account || identity is null || store is null ||
            !request.Messages.Any(message => message.Content.Parts.Any(part => part.Attachment is { } file &&
                (!useResponses || !IsPdf(file)))))
            return PrepareCore(request, useResponses);

        var roles = await identity.Roles(account, token);
        var policy = FeaturePolicies.Resolve(await store.Read(token), "file-extraction", roles);
        if (!policy.Enabled) throw new ChatAttachmentException("feature_disabled", 403);
        var admission = await store.Acquire(account, policy, token, withinChat: true);
        if (admission.LeaseId is null)
            throw new ChatAttachmentException("feature_limit_exceeded", 429, admission.RetryAfterSeconds);
        try { return PrepareCore(request, useResponses); }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await store.Release(admission.LeaseId, cleanup.Token); }
            catch (Exception exception) { logger?.LogWarning(exception, "File extraction lease cleanup deferred to expiry."); }
        }
    }

    private ChatRequest PrepareCore(ChatRequest request, bool useResponses)
    {
        var documents = new Dictionary<ChatAttachment, ChatContentPart>();
        return new ChatRequest
        {
            AccountId = request.AccountId,
            ConversationId = request.ConversationId,
            Model = request.Model,
            Stream = request.Stream,
            Vision = request.Vision,
            Messages = request.Messages.Select(message => new Message
            {
                Role = message.Role,
                Content = message.Content.Text is not null ? message.Content :
                    new ChatContent(null, message.Content.Parts.Select(part =>
                        PreparePart(part, message.Role, request.Vision, useResponses, documents)).ToArray())
            }).ToArray()
        };
    }

    private ChatContentPart PreparePart(ChatContentPart part, string role, bool vision, bool useResponses,
        Dictionary<ChatAttachment, ChatContentPart> documents)
    {
        if (part.ImageUrl is { } url)
        {
            if (role != "user" || !vision) throw new ChatAttachmentException("chat_image_unsupported");
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var separator = url.IndexOf(";base64,", StringComparison.Ordinal);
                if (separator < 0 || !ImageTypes.Contains(url[5..separator]))
                    throw new ChatAttachmentException("chat_attachment_unsupported");
                Decode(url.AsSpan(separator + 8));
            }
            return part;
        }

        if (part.Attachment is not { } file) return part;
        if (role != "user") throw new ChatAttachmentException("chat_attachment_unsupported");
        if (documents.TryGetValue(file, out var cached)) return cached;
        if (string.IsNullOrWhiteSpace(file.FileName) || file.FileName.Length > 255 ||
            file.FileName.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ChatAttachmentException("chat_attachment_invalid");

        var bytes = Decode(file.FileContent.AsSpan());
        ChatContentPart result;
        if (useResponses && IsPdf(file))
        {
            if (!vision) throw new ChatAttachmentException("chat_pdf_unsupported");
            if (!bytes.AsSpan().StartsWith("%PDF-"u8)) throw new ChatAttachmentException("chat_attachment_invalid");
            result = new(Attachment: file with { FileType = "application/pdf" });
        }
        else
        {
            try
            {
                var text = extractor.Extractor(new FileExtractionRequest
                {
                    FileName = file.FileName, FileType = file.FileType,
                    FileContent = file.FileContent, FileSize = bytes.Length
                });
                result = new(Text: $"\n filename: {file.FileName}\n parsed file content: {text}\n");
            }
            catch (NotSupportedException) { throw new ChatAttachmentException("chat_attachment_unsupported"); }
            catch (Exception exception) when (exception is InvalidDataException or FormatException or
                System.Xml.XmlException or UglyToad.PdfPig.Core.PdfDocumentFormatException)
            {
                throw new ChatAttachmentException("chat_attachment_invalid");
            }
        }
        documents.Add(file, result);
        return result;
    }

    private static bool IsPdf(ChatAttachment file) =>
        string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase);

    private static byte[] Decode(ReadOnlySpan<char> value)
    {
        const int limit = FileExtractionRequest.MaxFileBytes;
        if (value.IsEmpty || value.Length > (limit + 2) / 3 * 4)
            throw new ChatAttachmentException("chat_attachment_size");
        var bytes = new byte[value.Length / 4 * 3];
        if (!Convert.TryFromBase64Chars(value, bytes, out var written) || written == 0)
            throw new ChatAttachmentException("chat_attachment_invalid");
        if (written > limit) throw new ChatAttachmentException("chat_attachment_size");
        return bytes.AsSpan(0, written).ToArray();
    }
}
