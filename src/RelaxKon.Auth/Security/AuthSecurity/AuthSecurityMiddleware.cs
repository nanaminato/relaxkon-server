namespace RelaxKon.Auth.Security.AuthSecurity;

public sealed class AuthSecurityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AuthSecurityStore store, AuthSecurityState state)
    {
        var path = context.Request.Path.Value?.TrimEnd('/').ToLowerInvariant();
        var group = path switch
        {
            "/api/auth/login" => "login",
            "/api/auth/register" => "registration",
            "/api/auth/register/email-code" => "email",
            "/api/auth/password-reset/request" or "/api/auth/password-reset/confirm" => "reset",
            "/api/auth/refresh" or "/api/auth/logout" => "refresh",
            "/api/verification/generate" => "verification", _ => null
        };
        if (group is null) { await next(context); return; }
        AuthSecuritySettings settings;
        try { settings = await store.Read(context.RequestAborted); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
        catch (Exception)
        {
            // Fail closed without leaking database/configuration details or buffering retries.
            context.Response.StatusCode = 503; context.Response.Headers.RetryAfter = "1";
            await context.Response.WriteAsJsonAsync(new { code = "auth_security_unavailable" }, context.RequestAborted);
            return;
        }
        if (group == "login" && !settings.Enabled) { await next(context); return; }
        var ip = context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
        var (limit, seconds) = group switch
        {
            "login" => (settings.IpLimit, settings.IpWindowSeconds),
            "registration" => (settings.RegistrationLimit, settings.AnonymousWindowSeconds),
            "email" => (settings.EmailLimit, settings.AnonymousWindowSeconds),
            "reset" => (settings.ResetLimit, settings.AnonymousWindowSeconds),
            "refresh" => (settings.RefreshLimit, settings.RefreshWindowSeconds),
            _ => (settings.VerificationLimit, settings.AnonymousWindowSeconds)
        };
        var retry = state.Request(group, ip, limit, seconds, settings);
        if (retry > 0)
        {
            state.Record(group + "-limited", ip, null, retry, settings);
            context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await context.Response.WriteAsJsonAsync(new { code = "auth_rate_limited" }, context.RequestAborted); return;
        }
        await next(context);
    }
}
