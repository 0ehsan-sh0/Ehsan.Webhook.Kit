# 0004: Ingestion & Verification Architecture (Body Reader, Zero-Allocation HMAC, Timestamp & Extraction)

- **Status**: Accepted
- **Date**: 2026-09-22
- **Deciders**: Architecture Team, Engineering Lead

---

## Context

Subgroup 02 covers incoming HTTP webhook ingestion and cryptographic verification (Tasks 05 through 09). This includes capturing raw request bytes before framework mutation, constant-time HMAC signature verification supporting secret rotation, timestamp verification to prevent replay attacks, and business event ID and event type extraction across varied provider formats.

Key architectural trade-offs had to be resolved:
1. Size typing and exception behavior for `IWebhookBodyReader`.
2. Zero-allocation cryptographic HMAC execution and secret rotation.
3. Separation of concerns between signature verification and replay-window timestamp verification.
4. Extractor abstraction and JSON path navigation for event metadata.

---

## Decisions

### 1. `IWebhookBodyReader` Contract and Size Typing
- `IWebhookBodyReader.ReadRawBodyAsync(HttpContext context, long maxSizeBytes, CancellationToken ct)` uses `long maxSizeBytes` to match `WebhookKitOptions.MaxRequestBodySizeBytes`, avoiding integer overflow vulnerabilities on large uploads.
- Returns `byte[]` (or `ReadOnlyMemory<byte>` backed by array) to seamlessly populate `WebhookVerificationContext.RawBody` and `WebhookRecord.RawBody`.
- Uses `HttpRequest.EnableBuffering()` so downstream middleware and endpoint handlers can re-read the body if needed.
- If payload size exceeds `maxSizeBytes`, it immediately throws `WebhookPayloadTooLargeException(long actualBytes, long maxBytes)` without buffering excess bytes into memory, allowing the ASP.NET Core middleware/endpoint filter layer to uniformly map it to HTTP 413 (Payload Too Large).

### 2. Zero-Allocation HMAC Engine & Secret Rotation
- `HmacSignatureVerifier` implements `IWebhookSignatureVerifier` and uses modern .NET static cryptographic APIs (`HMACSHA256.HashData` and `HMACSHA512.HashData`) with stack-allocated spans (`stackalloc byte[32]` / `stackalloc byte[64]`).
- Performs constant-time comparison via `CryptographicOperations.FixedTimeEquals` to prevent timing attacks.
- Supports both Hexadecimal and Base64 encoded signature headers, stripping common algorithm prefixes (e.g. `sha256=`, `sha512=`).
- Supports secret rotation by checking the primary secret first, and falling back through `AdditionalSecrets` sequentially; verification succeeds on the first match and fails if none match.

### 3. Timestamp Verification as a Dedicated Abstraction
- Defined `IWebhookTimestampVerifier` in `WebhookKit.Abstractions` (or `WebhookKit.Core`) to validate timestamp headers against `WebhookTimestampOptions.Tolerance` using `IWebhookClock`.
- Automatically parses Unix epoch seconds (< 10 digits/threshold), Unix epoch milliseconds, and ISO-8601 strings.
- Separates timestamp verification from raw signature verification, allowing clean composition and independent testing. For providers with composite signature payloads (e.g. Stripe `t=...,v1=...`), the verifier or payload builder composes timestamp and raw bytes prior to HMAC hashing.

### 4. Event ID & Event Type Extraction Pipeline
- Implements `HeaderEventIdExtractor`, `HeaderEventTypeExtractor`, `JsonEventIdExtractor`, and `JsonEventTypeExtractor`.
- For JSON extraction, uses `JsonDocument` reading directly from `context.RawBody` with dot-notation path traversal (e.g., `id`, `event_id`, or `data.object.id`).
- Implements composite extractors (`CompositeWebhookEventIdExtractor` / `CompositeWebhookEventTypeExtractor`) providing automatic header-first fallback-to-JSON resolution.

---

## Considered Options

- **`int maxSizeBytes` vs `long maxSizeBytes`**: Rejected `int` because options and framework stream lengths are `long`; casting creates overflow risks.
- **Throwing exception vs writing HTTP 413 directly in body reader**: Rejected writing HTTP 413 directly in reader to preserve Single Responsibility Principle and allow flexible hosting/pipeline handling.
- **Instance-based `HMACSHA256` allocations**: Rejected in favor of static zero-allocation `HashData` methods with stack spans.
- **Monolithic signature-and-timestamp verifier**: Rejected because many providers (e.g., GitHub) do not sign timestamps or use different headers; keeping them distinct enables orthogonal composition.
- **Flat JSON regex/string search**: Rejected due to JSON escaping and structural corruption edge cases; `JsonDocument` is safe, compliant, and fast.

---

## Consequences

### Positive
- High throughput and minimal GC pressure via stack-allocated zero-allocation HMAC verification.
- Tamper-proof raw byte preservation with memory-exhaustion DoS protection via early-abort stream reading.
- Extensible, composable extraction supporting any provider convention without rigid vendor-specific couplings.

### Neutral / Trade-offs
- `JsonDocument` parses the UTF-8 bytes into pooled memory; for very large payloads, this uses temporary memory, but webhooks are typically small (< 256KB).
- Downstream endpoints must catch `WebhookPayloadTooLargeException` and map to HTTP 413.
