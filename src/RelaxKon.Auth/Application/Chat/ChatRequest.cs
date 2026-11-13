namespace RelaxKon.Auth.Application.Chat;

// Application input is independent of HTTP binding, JSON libraries and provider SDKs.
public sealed class ChatRequest
{
    public int? AccountId { get; init; }
    public string? ConversationId { get; init; }
    public Message[] Messages { get; init; } = [];
    public string? Model { get; init; }
    public bool Stream { get; init; }
    public bool Vision { get; set; }
}
public sealed class Message
{
    public string Role { get; init; } = "user";
    public ChatContent Content { get; init; } = ChatContent.Empty;
}
public sealed record ChatContent(string? Text, IReadOnlyList<ChatContentPart> Parts)
{
    public static readonly ChatContent Empty = new("", []);
    public static implicit operator ChatContent(string text) => new(text, []);
    public string PlainText => Text ?? string.Join("\n", Parts.Where(part => part.Text is not null).Select(part => part.Text));
}
public sealed record ChatContentPart(string? Text = null, string? ImageUrl = null, ChatAttachment? Attachment = null);

public sealed record ChatAttachment(string FileName, string? FileType, string FileContent, long FileSize = 0);
public sealed record ChatOutput(string? Text = null, ChatAttachment? Attachment = null);

public sealed class ChatAttachmentException(string code, int status = 400, int? retryAfter = null) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public int? RetryAfter { get; } = retryAfter;
}
