using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RelaxKon.Auth.Application.Analytics;
using RelaxKon.Auth.Security.FeatureAccess;

namespace RelaxKon.Auth.Controllers.Admin;

[ApiController, Authorize, Route("api/admin/operations")]
public sealed class OperationsController(OperationsAnalytics analytics, FeatureIdentity identity) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(int days = 7, string? feature = null, string? model = null,
        string? endpoint = null, CancellationToken token = default)
    {
        if (FeatureIdentity.AccountId(User) is not { } id ||
            !(await identity.Roles(id, token)).Contains("admin", StringComparer.Ordinal)) return Forbid();
        if (days is not (7 or 30)) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        return Ok(await analytics.Read(days, feature, model, endpoint, token));
    }
}
