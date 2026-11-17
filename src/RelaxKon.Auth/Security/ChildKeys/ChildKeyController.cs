using RelaxKon.Auth.Models.Suppliers;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RelaxKon.Auth.Application.Providers;
using RelaxKon.Auth.Data.Contexts;
using RelaxKon.Auth.Security.FeatureAccess;

namespace RelaxKon.Auth.Security.ChildKeys;

[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "admin")]
[Route("api/admin/child-keys")]
public sealed class ChildKeyController(KeyContext database, TimeProvider clock) : ControllerBase
{
    public sealed record CreateRequest([Range(1, int.MaxValue)] int SupplierKeyId,
        [Required, StringLength(100)] string Name, [Range(1, 365)] int ValidDays);
    public sealed record KeySummary(int Id, int SupplierKeyId, string Name, string Prefix,
        long CreatedAt, long ExpiresAt, long? RevokedAt);

    [HttpGet("suppliers")]
    public async Task<IActionResult> Suppliers(CancellationToken token)
    {
        var keys = await database.SupplierKeys!.AsNoTracking()
            .Where(key => key.Enable && key.ApiKey != null && key.ApiKey != "").ToListAsync(token);
        return Ok(keys.Where(IsAvailable).Select(key => new
        {
            id = key.SupplierKeyId,
            provider = ((HandlerType)key.RequestIdentifier).ToString(),
            name = SupplierName(key),
            baseUrl = NativeProvider.BaseAddress(key)!.ToString()
        }));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token)
    {
        if (FeatureIdentity.AccountId(User) is not int account) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        return Ok(await database.Set<ChildApiKey>().AsNoTracking().Where(key => key.AccountId == account)
            .OrderByDescending(key => key.Id)
            .Select(key => new KeySummary(key.Id, key.SupplierKeyId, key.Name, key.Prefix,
                key.CreatedAt, key.ExpiresAt, key.RevokedAt)).ToListAsync(token));
    }

    public sealed record KeyPageQuery(
        [RegularExpression("^(active|attention|history)$")] string Tab = "active",
        [StringLength(100)] string? Search = null,
        [Range(1, int.MaxValue)] int? SupplierId = null,
        [Range(1, int.MaxValue)] int Page = 1);

    public sealed record SupplierFilter(int Id, string Name);
    public sealed record KeyPage(IReadOnlyList<KeySummary> Items, int Page, int Total, IReadOnlyList<SupplierFilter> Suppliers);

    [HttpGet("page")]
    public async Task<ActionResult<KeyPage>> ListPage([FromQuery] KeyPageQuery query, CancellationToken token)
    {
        if (FeatureIdentity.AccountId(User) is not int account) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        const int pageSize = 20;
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var owned = database.Set<ChildApiKey>().AsNoTracking().Where(key => key.AccountId == account);
        var suppliers = await database.SupplierKeys!.AsNoTracking()
            .Where(supplier => owned.Any(key => key.SupplierKeyId == supplier.SupplierKeyId))
            .OrderBy(supplier => supplier.SupplierKeyId).ToListAsync(token);
        var availableIds = suppliers.Where(IsAvailable).Select(supplier => supplier.SupplierKeyId).ToArray();

        var keys = query.Tab switch
        {
            "history" => owned.Where(key => key.RevokedAt != null || key.ExpiresAt <= now),
            "attention" => owned.Where(key => key.RevokedAt == null && key.ExpiresAt > now &&
                !availableIds.Contains(key.SupplierKeyId)),
            _ => owned.Where(key => key.RevokedAt == null && key.ExpiresAt > now &&
                availableIds.Contains(key.SupplierKeyId))
        };
        if (query.SupplierId is int supplierId) keys = keys.Where(key => key.SupplierKeyId == supplierId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim().ToLowerInvariant();
            keys = keys.Where(key => key.Name.ToLower().Contains(search) || key.Prefix.ToLower().Contains(search));
        }

        var total = await keys.CountAsync(token);
        var lastPage = total == 0 ? 1 : (total - 1) / pageSize + 1;
        var page = Math.Min(query.Page, lastPage);
        var sorted = query.Tab == "history"
            ? keys.OrderByDescending(key => key.RevokedAt ?? key.ExpiresAt).ThenByDescending(key => key.Id)
            : keys.OrderBy(key => key.ExpiresAt).ThenBy(key => key.Id);
        var items = await sorted.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(key => new KeySummary(key.Id, key.SupplierKeyId, key.Name, key.Prefix,
                key.CreatedAt, key.ExpiresAt, key.RevokedAt)).ToListAsync(token);

        return Ok(new KeyPage(items, page, total, suppliers.Select(supplier =>
            new SupplierFilter(supplier.SupplierKeyId, SupplierName(supplier))).ToArray()));
    }

    private static bool IsAvailable(SupplierKey supplier) => supplier.Enable &&
        !string.IsNullOrWhiteSpace(supplier.ApiKey) && NativeProvider.BaseAddress(supplier) is not null;

    private static string SupplierName(SupplierKey supplier)
    {
        var provider = (HandlerType)supplier.RequestIdentifier;
        if (Uri.TryCreate(supplier.BaseUrl?.Trim(), UriKind.Absolute, out var configured))
        {
            return configured.IdnHost.ToLowerInvariant() switch
            {
                "api.deepseek.com" => "DeepSeek",
                "api.openai.com" => "OpenAI",
                "api.anthropic.com" => "Anthropic",
                "generativelanguage.googleapis.com" => "Google",
                var host when host.Length > 0 => host,
                _ => provider.ToString()
            };
        }
        return provider == HandlerType.Openai ? "OpenAI" : provider.ToString();
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateRequest request, CancellationToken token)
    {
        if (FeatureIdentity.AccountId(User) is not int account) return Unauthorized();
        var parent = await database.SupplierKeys!.AsNoTracking().SingleOrDefaultAsync(
            key => key.SupplierKeyId == request.SupplierKeyId && key.Enable, token);
        if (parent is null || NativeProvider.BaseAddress(parent) is null || string.IsNullOrWhiteSpace(parent.ApiKey))
            return BadRequest(new { code = "supplier_unavailable" });

        var secret = ChildApiKey.Generate();
        var now = clock.GetUtcNow();
        var key = new ChildApiKey
        {
            AccountId = account,
            SupplierKeyId = parent.SupplierKeyId,
            Name = request.Name.Trim(),
            Prefix = secret[..11],
            SecretHash = ChildApiKey.Hash(secret),
            CreatedAt = now.ToUnixTimeSeconds(),
            ExpiresAt = now.AddDays(request.ValidDays).ToUnixTimeSeconds()
        };
        database.Add(key);
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException)
        {
            if (!await database.SupplierKeys!.AnyAsync(item => item.SupplierKeyId == parent.SupplierKeyId, token))
                return Conflict(new { code = "supplier_unavailable" });
            throw;
        }
        Response.Headers.CacheControl = "no-store";
        return StatusCode(StatusCodes.Status201Created, new
        {
            secret,
            key = new KeySummary(key.Id, key.SupplierKeyId, key.Name, key.Prefix, key.CreatedAt, key.ExpiresAt, null)
        });
    }

    [HttpDelete("{id:int:min(1)}")]
    public async Task<IActionResult> Revoke(int id, CancellationToken token)
    {
        if (FeatureIdentity.AccountId(User) is not int account) return Unauthorized();
        var keys = database.Set<ChildApiKey>().Where(key => key.Id == id && key.AccountId == account);
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var updated = await keys.Where(key => key.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(key => key.RevokedAt, now), token);
        return updated > 0 || await keys.AnyAsync(token) ? NoContent() : NotFound();
    }
}
