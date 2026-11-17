using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RelaxKon.Auth.Application.Providers;
using RelaxKon.Auth.Models.Suppliers;

namespace RelaxKon.Auth.Security.ChildKeys;

public sealed class ChildApiKey
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public int SupplierKeyId { get; set; }
    public SupplierKey SupplierKey { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string SecretHash { get; set; } = "";
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public long? RevokedAt { get; set; }

    public static string Generate() => "rk_" + Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
        RandomNumberGenerator.GetBytes(32));

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}

internal static partial class NativeProvider
{
    public static Uri? BaseAddress(SupplierKey key)
    {
        var configured = key.BaseUrl?.Trim();
        if (string.IsNullOrEmpty(configured))
            configured = (HandlerType)key.RequestIdentifier switch
            {
                HandlerType.Openai => "https://api.openai.com",
                HandlerType.Anthropic => "https://api.anthropic.com",
                HandlerType.Google => "https://generativelanguage.googleapis.com",
                _ => null
            };

        return IsSupported((HandlerType)key.RequestIdentifier) &&
            Uri.TryCreate(configured, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri : null;
    }

    private static bool IsSupported(HandlerType type) => type is HandlerType.Openai or HandlerType.Anthropic
        or HandlerType.Google or HandlerType.Alibaba or HandlerType.ApiMart or HandlerType.Novita;

    public static bool Allows(HandlerType provider, string method, string path)
    {
        if (path.Split('/').Any(segment => segment is "." or ".." or "") || !SafePath().IsMatch(path))
            return false;

        if (provider == HandlerType.Google)
            return method switch
            {
                "POST" => GeminiOperation().IsMatch(path),
                "GET" => GeminiRead().IsMatch(path),
                _ => false
            };

        if (provider == HandlerType.Novita)
            return method switch
            {
                "POST" => path is "v3/openai/chat/completions" or "v3/async/txt2img" or "v3/async/img2img",
                "GET" => path is "v3/openai/models" or "v3/async/task-result",
                _ => false
            };

        if (provider == HandlerType.Anthropic)
            return method == "POST" && path is "v1/messages" or "v1/messages/count_tokens" ||
                method == "GET" && (path == "v1/models" || IsResource(path, "v1/models/"));

        return method switch
        {
            "POST" => path is "v1/chat/completions" or "v1/responses" or "v1/completions"
                or "v1/embeddings" or "v1/images/generations" or "v1/images/edits" or "v1/images/variations"
                or "v1/audio/speech" or "v1/audio/transcriptions" or "v1/audio/translations"
                or "v1/videos" ||
                provider == HandlerType.Alibaba && path is "api/v1/services/aigc/text-generation/generation"
                    or "api/v1/services/aigc/multimodal-generation/generation"
                    or "api/v1/services/aigc/text2image/image-synthesis",
            "GET" => path == "v1/models" || IsResource(path, "v1/models/") ||
                IsResource(path, "v1/responses/") || IsResource(path, "v1/videos/") ||
                ((provider is HandlerType.ApiMart or HandlerType.Alibaba) &&
                    (IsResource(path, "v1/tasks/") || IsResource(path, "api/v1/tasks/"))),
            _ => false
        };
    }

    private static bool IsResource(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > prefix.Length &&
        !path.AsSpan(prefix.Length).Contains('/');

    public static Uri? Target(Uri upstream, string path, string query)
    {
        var prefix = upstream.AbsolutePath.TrimEnd('/');
        var version = prefix[(prefix.LastIndexOf('/') + 1)..];
        var configuredApiPath = prefix.EndsWith("/v3/openai", StringComparison.Ordinal) ? "v3/openai"
            : ApiVersion().IsMatch(version) ? version : null;
        if (configuredApiPath is not null)
        {
            var apiPrefix = configuredApiPath + "/";
            if (!path.StartsWith(apiPrefix, StringComparison.Ordinal)) return null;
            path = path[apiPrefix.Length..];
        }
        return new Uri(upstream.GetLeftPart(UriPartial.Authority) + prefix + "/" + path + query);
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_./:-]+\z")]
    private static partial Regex SafePath();

    [GeneratedRegex(@"\Av[0-9]+(?:(?:beta|alpha)[0-9]*)?\z")]
    private static partial Regex ApiVersion();

    [GeneratedRegex(@"\Av(?:1|1beta|1alpha)/models/[A-Za-z0-9_.-]+:(?:generateContent|streamGenerateContent|countTokens|embedContent|batchEmbedContents|predict|predictLongRunning)\z")]
    private static partial Regex GeminiOperation();

    [GeneratedRegex(@"\Av(?:1|1beta|1alpha)/models(?:/[A-Za-z0-9_.-]+(?:/operations/[A-Za-z0-9_.-]+)?)?\z")]
    private static partial Regex GeminiRead();
}
