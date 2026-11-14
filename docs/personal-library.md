# Personal library / 资料空间

Authenticated users can keep private files independently of chat messages. Binary bytes and metadata are stored in `LibraryFiles` in the existing MySQL or SQLite database. A second device sees the same owner-scoped list on refresh; there is no separate cloud provider and no automatic publication. This first version is a file library, not automatic document indexing or retrieval-augmented generation.

## API

All routes require the existing bearer authentication and use the authenticated account ID, never an account ID supplied by the caller:

- `GET /api/library`: `{ items, usedBytes, maxBytes, maxFileBytes, maxFiles }`, metadata only.
- `POST /api/library`: multipart field `file`; returns `201` and metadata.
- `GET /api/library/{id}/content`: attachment download, `private, no-store`, `nosniff`. URLs are not public share links.
- `PATCH /api/library/{id}`: `{ name, version }`; increments the version.
- `DELETE /api/library/{id}?version=N`: deletes only the current owner/version. Permanent file deletion requires confirmation in the client; it does not edit existing chat attachments.

Metadata: `{ id, name, contentType, size, createdAt, updatedAt, version }`. Dates are UTC. Foreign-owner and missing IDs both return `404`; stale rename/delete returns `409` so another device's edit is not silently lost. Clients should refresh after conflict, not retry a destructive request automatically. Uploads are not automatically retried after a lost response; refresh the list first. An empty/oversized file returns `413`, exceeded storage/file-count quota `507`, and exhausted bounded admission `429` with `Retry-After: 1`.

## Limits and deployment

`Library:MaxFileBytes` defaults to 10 MiB (hard upper bound 10 MiB); `Library:MaxBytes` defaults to 100 MiB per account (maximum configurable 1 GiB); `Library:MaxFiles` defaults to 100 (maximum 1000). Configure via JSON or environment variables, e.g. `Library__MaxBytes`. Changes require a service restart. Reducing a quota does not delete files; existing items stay available but new uploads are rejected until usage fits.

This is a **single-instance** implementation. One nonqueued write admission gate protects quota check/write transactions and four read slots bound concurrent metadata/database download reads; download slots remain held until the response stream is disposed. No unbounded account-lock dictionary or queue is created. A pre-model-binding resource filter admits at most two multipart uploads, rejecting excess requests before buffering. Reverse-proxy request/rate/concurrency limits remain necessary at the network edge. Multipart bytes are not Base64 embedded into conversation sync, avoiding its expansion and history-sync failure coupling. Include the library endpoint explicitly in deployment body limits (10 MiB plus multipart overhead), rather than broadly raising every anonymous endpoint limit. MySQL `max_allowed_packet` must also support the configured maximum binary insert (default 64 MiB is sufficient).

Files are private storage, **not malware-scanned**. Downloads force attachment disposition; do not render arbitrary HTML/SVG inline. Client selection for chat is an explicit action and creates the existing bounded chat attachment; changing/deleting the library original does not rewrite older conversations or shared exports. Backups must include the database; database loss loses file bytes. Protect backups as private user data.

## Database upgrade

Fresh `--init-db` includes the new table. Existing deployments must back up, stop the application, and apply the matching additive maintenance SQL:

- MySQL: `src/RelaxKon.Auth/Resources/library/mysql-upgrade.sql`
- SQLite: `src/RelaxKon.Auth/Resources/library/sqlite-upgrade.sql`

Do **not** rerun `--init-db` on an existing database. The scripts do not alter existing account/chat rows and intentionally fail on repeated application rather than conceal a mismatched schema. Startup preflight identifies a missing table; normal startup never creates/changes the schema. Rolling back application binaries leaves the additive table intact; do not drop it if users have saved files.

## Verification

`LibraryTests` covers SQLite file bytes, independent-context/device reads, owner isolation on all actions, absent identity, optimistic conflicts, invalid names, quotas, bounded admission and additive upgrade preservation. `LibraryMySqlTests` uses an isolated generated MySQL database and is selected explicitly in database CI. Neither suite implies a production deployment or browser/device acceptance test.
