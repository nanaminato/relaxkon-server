# Single-instance authentication security

Authentication protection runs in one process. No Redis or distributed counters are required. Existing password hashing, refresh-token rotation/replay revocation, security stamps, verified registration and password-reset tokens remain in place.

## Defaults and semantics

| Control | Default |
|---|---|
| Login requests per source IP | 10 / 60 seconds |
| Failed passwords per account | 5 / 900 seconds |
| Failed passwords per IP + account | 5 / 300 seconds |
| Pair failure cooldown | 30 seconds, doubles up to 900 seconds |
| Account-wide pressure | at most one password-check probe every 2 seconds (configurable 1–5) after the failure threshold |
| Concurrent password checks | 8, no waiting queue |
| Counter entries | 10,000 (configurable 100–100,000) |
| Recent events | 500 (configurable 10–5,000) |
| Register / email-code / password-reset / captcha-generation per IP | 30 / 10 / 10 / 30 per 900 seconds |
| Refresh and logout combined per IP | 60 / 60 seconds |

**Five account failures do not impose a 15-minute global account lock.** Account pressure uses the short probe interval. Correct credentials at an available probe succeed and clear that account's failures and the successful IP/account pair. A malicious request cannot extend the current probe deadline merely by being rejected. The long exponential cooldown applies only to an IP/account pair. Shared-IP request budgets are never cleared by successful login. Under active attacks a legitimate request can still need to retry a probe or a shared-IP limit; this is a deliberate bounded anti-lockout compromise, not a claim of perfect availability under denial of service.

Windows are fixed, not sliding. Counters are locked atomically and stored in a dedicated size-limited `IMemoryCache`; capacity exhaustion rejects new tracked work with `429` and `Retry-After: 1` rather than creating unbounded IP partitions. This bounded custom admission replaces the old static `RateLimiter` policies so settings can change immediately. Password hashing is separately concurrency-bounded even when login throttling is disabled. Registration, reset, refresh and other anonymous endpoint limits stay enabled when the login switch is off. Password-reset recipient cooldowns are atomic and use the same bounded state in production.

Unknown usernames and wrong passwords take the password-verification path and return the same unauthorized response (or the same state-dependent 429). A dummy Identity hash is verified for nonexistent users; legacy plaintext verification also performs dummy hashing work before upgrade. Network/DB timing can still differ, so this is mitigation, not a constant-time HTTP guarantee. The compatibility captcha endpoint is not an authentication security boundary.

## Administration and persistence

The web admin page **Authentication security** supports live settings, recent events, current restrictions and manual release. Every API checks current database roles and the dynamic `auth-security-management` grant; an authenticated administrator with the enabled grant is required. Old policy documents receive the administrator default for this new feature, and old clients cannot accidentally erase it by saving an older policy shape.

- `GET /api/auth-security/settings`
- `PUT /api/auth-security/settings` (complete settings with current `revision`; stale writes return 409)
- `GET /api/auth-security/status`
- `DELETE /api/auth-security/restrictions/{id}`

Settings use `FeatureAccessPolicies` row **2**, separate from feature grants in row **1**. No new table or schema migration is needed. Parameterized provider-specific upsert statements support MySQL and SQLite. The last 100 administrative change/release events persist with the document; high-volume login events and short-term counters stay in memory and clear on restart. A settings change resets temporary counters so old windows cannot impose stale limits. Manual release clears only its target. Status returns at most 500 active restrictions and the configured recent-event bound.

Events never contain passwords, tokens or raw usernames. Account correlation uses a per-process random-key HMAC and therefore cannot be correlated across restarts. IP addresses remain visible to authorized administrators. Keep access to this page restricted. Status and settings responses use `Cache-Control: no-store`.

## Deployment safety

1. Configure `ForwardedHeaders:KnownProxies` with only the actual reverse proxy addresses. `ForwardLimit` defaults to 1. Arbitrary `X-Forwarded-For` values from untrusted peers do not determine rate-limit keys. Do not enable globally trusted forwarded headers via hosting environment overrides.
2. Keep HTTPS and edge request/connection limits. A local login limit is not network-level DDoS protection. Anonymous non-login operations have per-IP/recipient limits, not a global mail-service budget.
3. The checked-in private-network database/cache credentials were removed. Supply existing deployment values through environment-specific configuration. **If any formerly committed credential was real, rotate it separately**, including credentials exposed in Git history; this change does not rewrite history or rotate production secrets.
4. JWT signing key and refresh-token pepper must be distinct random values of at least 32 UTF-8 bytes. Example placeholders are rejected at startup. Generate each separately with `openssl rand -hex 32`. Preserve already-strong deployed secrets to retain compatibility; deployments relying on weak/example secrets must deliberately replace them, invalidating prior sessions as appropriate.
5. Stored Identity hash strings can no longer authenticate as their own password through the legacy plaintext fallback. Normal existing hashes remain valid. Malformed Identity-shaped legacy values require password reset rather than being treated as plaintext.
6. Runtime setting changes are supported through the API in one instance. Editing the JSON row externally requires restarting the process to refresh its cached configuration. Do not deploy multiple independent replicas expecting shared counters.

## Verification

Tests cover counter races/capacity/expiry/restart, account pressure and pair cooldowns, correct/incorrect/nonexistent-user login, password-hash fallback, JWT configuration, trusted proxy behavior, settings concurrency/persistence and permission isolation. SQLite tests use temporary databases. The explicit MySQL suite requires `TURBO_TEST_MYSQL` pointing to an isolated test server; it creates/drops generated test databases, never a production database. The database-compatibility PR workflow runs both providers. No production configuration or data is modified by this implementation.
