using RelaxKon.Auth.Security.AuthSecurity;
using RelaxKon.Auth.Contracts.Common;
using RelaxKon.Auth.Localization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RelaxKon.Auth.Contracts.Auth;
using RelaxKon.Auth.Data.Contexts;
using RelaxKon.Auth.Models.Accounts;
using RelaxKon.Auth.Models.Config;
using RelaxKon.Auth.Repositories.Accounts;
using RelaxKon.Auth.Security;

namespace RelaxKon.Auth.Controllers.Auth;

[ApiController, RequestSizeLimit(16384)]
[Route("api/[controller]")]
public class AuthController : Controller
{
    private readonly AuthContext _context;
    private readonly JwtSettings _jwtSettings;
    private readonly IAccountRepository _accountRepository;
    private readonly IAccountPasswordService _passwordService;
    private readonly IAuthSessionService _sessions;
    private readonly AuthSecurityState? _security;
    private readonly AuthSecurityStore? _securityStore;
    private static readonly Account Dummy = CreateDummy();
    private static Account CreateDummy() { var a = new Account(); a.Password = new AccountPasswordService().Hash(a, Guid.NewGuid().ToString()); return a; }

    public AuthController(AuthContext context, IOptions<JwtSettings> jwtSettings,
        IAccountRepository accountRepository, IAccountPasswordService passwordService, IAuthSessionService sessions, AuthSecurityState? security = null, AuthSecurityStore? securityStore = null)
    {
        _context = context;
        _jwtSettings = jwtSettings.Value;
        _accountRepository = accountRepository;
        _passwordService = passwordService;
        _sessions = sessions;
        _security = security; _securityStore = securityStore;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<TokenResponse>> Login([FromBody] LoginRequest body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password)) return Unauthorized();

        var settings = _securityStore is null ? new AuthSecuritySettings() : await _securityStore.Read(cancellationToken);
        var ip = HttpContext.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
        if (_security is not null && !_security.BeginGlobalLogin(settings))
        {
            _security.Record("login-limited", ip, null, 1, settings);
            return Limited(1);
        }
        try
        {
            // Resolve the database's own collation first: spelling aliases must share one failure budget.
            // Both database lookup and password work are bounded by the global concurrent-login lease.
            var identifier = body.Username.Trim();
            var found = await _context.Accounts.FirstOrDefaultAsync(u => u.Username == identifier, cancellationToken);
            if (found is null && identifier.Contains('@'))
            {
                var matches = await _context.Accounts.Where(u => u.Email == identifier)
                    .OrderBy(u => u.AccountId).Take(2).ToListAsync(cancellationToken);
                if (matches.Count == 1) found = matches[0];
            }
            var accountKey = _security?.AccountKey(found?.Username ?? body.Username);
            var admission = _security?.AdmitAccount(ip, accountKey!, settings) ?? (Admitted: true, RetryAfterSeconds: 0);
            if (!admission.Admitted)
            {
                _security?.Record("login-limited", ip, accountKey, admission.RetryAfterSeconds, settings);
                return Limited(admission.RetryAfterSeconds);
            }
            var account = await ValidateUserAsync(found, body.Password, cancellationToken);
            if (account is null)
            {
                var retry = _security?.Failure(ip, accountKey!, settings) ?? 0;
                return retry > 0 ? Limited(retry) : Unauthorized();
            }
            _security?.Success(ip, accountKey!, settings);
            var issue = await _sessions.CreateSessionAsync(account, body.DeviceName, GetIpAddress(), cancellationToken);
            return Ok(await BuildTokenResponseAsync(account, issue, cancellationToken));
        }
        finally { _security?.EndLogin(); }
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<TokenResponse>> Refresh([FromBody] RefreshTokenRequest body, CancellationToken cancellationToken)
    {
        var rotation = await _sessions.RotateAsync(body.RefreshToken ?? string.Empty, body.DeviceName, GetIpAddress(),
            cancellationToken);
        if (!rotation.Succeeded) return Unauthorized();

        return Ok(await BuildTokenResponseAsync(rotation.Account!, rotation.Issue!, cancellationToken));
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout([FromBody] RefreshTokenRequest body, CancellationToken cancellationToken)
    {
        await _sessions.RevokeByRefreshTokenAsync(body.RefreshToken ?? string.Empty, cancellationToken);
        return NoContent();
    }

    [HttpPost("logout-all")]
    [Authorize]
    public async Task<IActionResult> LogoutAll(CancellationToken cancellationToken)
    {
        var accountId = GetCurrentAccountId();
        if (accountId is null) return Unauthorized();

        await _sessions.InvalidateAllAsync(accountId.Value, cancellationToken);
        return NoContent();
    }

    [HttpGet("sessions")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<SessionResponse>>> Sessions(CancellationToken cancellationToken)
    {
        var accountId = GetCurrentAccountId();
        if (accountId is null) return Unauthorized();

        return Ok((await _sessions.GetActiveSessionsAsync(accountId.Value, cancellationToken)).Select(session =>
            new SessionResponse(session.SessionId, session.DeviceName, session.IpAddress, session.CreatedAt, session.LastUsedAt, session.ExpiresAt)).ToList());
    }

    [HttpDelete("sessions/{sessionId:guid}")]
    [Authorize]
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken cancellationToken)
    {
        var accountId = GetCurrentAccountId();
        if (accountId is null) return Unauthorized();

        await _sessions.RevokeSessionAsync(accountId.Value, sessionId, cancellationToken);
        return NoContent();
    }

    private async Task<Account?> ValidateUserAsync(Account? account, string password, CancellationToken cancellationToken)
    {
        if (account is null)
        {
            _passwordService.Verify(Dummy, password);
            return null;
        }

        var verification = _passwordService.Verify(account, password);
        if (verification == PasswordVerificationState.Invalid) return null;

        if (verification == PasswordVerificationState.ValidNeedsUpgrade)
        {
            account.Password = _passwordService.Hash(account, password);
            await _context.SaveChangesAsync(cancellationToken);
        }

        return account;
    }

    private ObjectResult Limited(int seconds)
    {
        Response.Headers.RetryAfter = Math.Max(1, seconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return StatusCode(429, new CodeResponse("auth_rate_limited"));
    }

    private async Task<TokenResponse> BuildTokenResponseAsync(Account account, RefreshTokenIssue issue, CancellationToken cancellationToken)
    {
        var accessToken = await GenerateAccessTokenAsync(account, issue.SessionId, cancellationToken);
        return new TokenResponse(accessToken, accessToken, issue.RefreshToken, account.AccountId,
            issue.SessionId, _jwtSettings.AccessTokenMinutes * 60, issue.RefreshTokenExpiresAt);
    }

    private async Task<string> GenerateAccessTokenAsync(Account account, Guid sessionId, CancellationToken cancellationToken)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SecretKey!));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var roles = await _context.AccountRoles!.AsNoTracking()
            .Where(a => a.AccountId == account.AccountId)
            .Select(a => (a.Role!.Code ?? a.Role.Name)!)
            .ToListAsync(cancellationToken);
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, account.Username!),
            new(ClaimTypes.NameIdentifier, account.AccountId.ToString()),
            new(ClaimTypes.Sid, sessionId.ToString()),
            new("sv", account.SecurityStamp),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        roles.ForEach(role => claims.Add(new Claim(ClaimTypes.Role, role)));

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(_jwtSettings.AccessTokenMinutes),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private int? GetCurrentAccountId() => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var accountId)
        ? accountId
        : null;

    private string? GetIpAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
