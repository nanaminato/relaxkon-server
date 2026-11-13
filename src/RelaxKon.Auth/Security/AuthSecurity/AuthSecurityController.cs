using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RelaxKon.Auth.Contracts.Common;
using RelaxKon.Auth.Security.FeatureAccess;

namespace RelaxKon.Auth.Security.AuthSecurity;

[ApiController, Authorize, Route("api/auth-security")]
public sealed class AuthSecurityController(AuthSecurityStore store, AuthSecurityState state, FeatureIdentity identity, FeatureAccessStore policies) : ControllerBase
{
    private async Task<int?> Administrator(CancellationToken token)
    {
        if (FeatureIdentity.AccountId(User) is not { } id) return null;
        var roles = await identity.Roles(id, token);
        return roles.Contains("admin", StringComparer.Ordinal) && FeaturePolicies.Resolve(await policies.Read(token), "auth-security-management", roles).Enabled ? id : null;
    }
    [HttpGet("settings")]
    public async Task<IActionResult> Get(CancellationToken token)
    {
        if (await Administrator(token) is null) return Forbid();
        Response.Headers.CacheControl = "no-store"; return Ok(await store.Read(token));
    }
    [HttpPut("settings"), RequestSizeLimit(16384)]
    public async Task<IActionResult> Put(AuthSecuritySettings settings, CancellationToken token)
    {
        var actor = await Administrator(token); if (actor is null) return Forbid();
        if (!settings.IsValid()) return BadRequest(new CodeResponse("invalid_auth_security_settings"));
        if (!await store.Save(settings, actor.Value, token)) return Conflict(new CodeResponse("auth_security_conflict"));
        return Ok(await store.Read(token));
    }
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken token)
    {
        if (await Administrator(token) is null) return Forbid();
        Response.Headers.CacheControl = "no-store"; return Ok(state.Status(await store.Read(token), store.Audit));
    }
    [HttpDelete("restrictions/{id}")]
    public async Task<IActionResult> Clear(string id, CancellationToken token)
    {
        var actor = await Administrator(token); if (actor is null) return Forbid();
        if (id.Length > 256) return BadRequest();
        if (!await store.Save(await store.Read(token), actor.Value, token, "restriction-cleared", false))
            return Conflict(new CodeResponse("auth_security_conflict"));
        state.Clear(id);
        return NoContent();
    }
}
