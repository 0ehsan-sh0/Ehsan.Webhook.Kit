# 0002: Foundations Core Domain (RawBody bytes, IDs, strict store contract)

Foundations Task 02 deviates from WEBHOOKKIT.md §8.1 (`string? RawBody`) by storing `byte[]? RawBody` as source of truth with headers as read-only dictionary, IDs as opaque strings (`Id` ULID-formatted via `Guid.CreateVersion7`), verification as `WebhookVerificationResult(IsValid, FailureReason)` + `WebhookVerificationContext(provider, bytes, headers)`, and a strict `IWebhookStore` contract requiring non-null `provider/eventId` with no hash-fallback in this slice — because HMAC breaks on any encoding shift, provider IDs are opaque, and null-key fallbacks would corrupt Redis/EF unique indexes.

## Considered Options
- `string? RawBody` only (rejected: encoding drift breaks signatures, kills binary payloads).
- Strongly-typed `WebhookId` struct (rejected: adds API surface with no routing benefit; string ULID keeps providers flexible).
- Null-tolerant store with `provider+timestamp+bodyHash` fallback now (rejected: deferred to Subgroup 03 deduplication pipeline where replay policy lives).

## Consequences
- `WebhookKit.Abstractions` stays dependency-free; `CONTEXT.md` updated (Raw Body Capture, Webhook ID, Verification Result/Context).
- Missing-EventId requests fail fast in foundations; fallback strategy must be explicitly designed in Task 11.
