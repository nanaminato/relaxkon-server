using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelaxKon.Auth.Data.Contexts;
using RelaxKon.Auth.Models.Suppliers;
using RelaxKon.Auth.Security.FeatureAccess;

namespace RelaxKon.Auth.Security.ChildKeys;

public sealed class ChildKeyAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    KeyContext database, FeatureIdentity identity, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ChildApiKey";

    public static SupplierKey Parent(HttpContext context) => (SupplierKey)context.Items[typeof(ChildKeyAuthentication)]!;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var secret = ReadSecret(Request);
        if (secret is null || secret.Length != 46 || !secret.StartsWith("rk_", StringComparison.Ordinal))
            return AuthenticateResult.Fail("Invalid child Key.");

        var hash = ChildApiKey.Hash(secret);
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var key = await database.Set<ChildApiKey>().AsNoTracking().Include(item => item.SupplierKey)
            .SingleOrDefaultAsync(item => item.SecretHash == hash && item.RevokedAt == null &&
                item.ExpiresAt > now && item.SupplierKey.Enable, Context.RequestAborted);
        if (key is null || string.IsNullOrWhiteSpace(key.SupplierKey.ApiKey) ||
            NativeProvider.BaseAddress(key.SupplierKey) is null ||
            !(await identity.Roles(key.AccountId, Context.RequestAborted)).Contains("admin", StringComparer.Ordinal))
            return AuthenticateResult.Fail("Invalid child Key.");

        Context.Items[typeof(ChildKeyAuthentication)] = key.SupplierKey;
        var account = key.AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account)], SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private static string? ReadSecret(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Authorization", out var authorization))
            return AuthenticationHeaderValue.TryParse(authorization.ToString(), out var header) &&
                header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ? header.Parameter : null;
        if (request.Headers.TryGetValue("x-api-key", out var anthropic)) return anthropic.ToString();
        if (request.Headers.TryGetValue("x-goog-api-key", out var google)) return google.ToString();
        return null;
    }
}
