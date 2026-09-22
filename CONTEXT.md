# WebhookKit Ubiquitous Language & Domain Glossary

This document serves as the single source of truth for ubiquitous language, domain concepts, and terminology across WebhookKit.

---

## 1. Core Concepts & Identity

### Webhook Record (`WebhookRecord`)
The persistent or in-flight representation of an ingested webhook delivery. It captures raw HTTP metadata (path, method, headers as read-only dictionary, content-type, content-length), the exact unmodified raw payload bytes (`byte[]? RawBody`) as source of truth, provider identification, delivery timestamps, processing state, attempt count, and diagnostic failure reasons.

### Webhook Context (`WebhookContext`)
The execution context passed into application event handlers. It provides safe, read-only access to extracted metadata (Webhook ID, Provider ID, Event ID, Event Type, Timestamp, Headers) and deserialization helpers for strongly typed event payloads, deliberately shielding cryptographic secrets and raw buffer details from business handlers.

### Webhook ID (`WebhookId`)
A ULID-formatted string identifier (`required string Id`, generated via `Guid.CreateVersion7`) created by WebhookKit at the instant of HTTP ingress. It uniquely identifies an *individual HTTP transmission* attempt from a provider.
_Avoid_: Event ID, correlation ID.

### Event ID (`EventId`)
The upstream provider's unique identifier for a business event (e.g., Stripe `evt_12345` or GitHub delivery GUID). Extracted from custom headers (e.g., `X-Event-Id`, `X-GitHub-Delivery`) or JSON payload properties (e.g., `id`, `event_id`). One Event ID may arrive across multiple Webhook IDs due to provider retries.

### Event Type (`EventType`)
The categorical classification of the webhook event (e.g., `payment.succeeded`, `customer.subscription.deleted`). Extracted from headers or payload bodies to route execution to registered event handlers (`IWebhookHandler<T>`).

---

## 2. Security, Integrity & Replay

### Raw Body Capture
The exact byte-for-byte replica (`byte[]`) of the inbound HTTP request stream preserved before any JSON deserialization or stream mutation occurs. It is essential because cryptographic signature verification fails if a single whitespace or line break is modified.
_Avoid_: decoded string body as source of truth.

### Signature Verification (`IWebhookSignatureVerifier`)
Cryptographic authentication of the payload confirming that the request originated from the authentic upstream provider. Typically computed using HMAC (HMAC-SHA256, HMAC-SHA512) over the raw request bytes using a shared secret.

### Verification Result (`WebhookVerificationResult`)
The outcome of signature verification carrying validity plus a non-sensitive failure reason for HTTP mapping. _Avoid_: throwing exceptions for expected rejections, leaking secrets in reasons.

### Verification Context (`WebhookVerificationContext`)
The inputs to signature verification: provider name, raw body bytes, and request headers. It carries no secrets.
_Avoid_: deserialized payload, string body.

### Constant-Time Comparison
A comparison routine (`CryptographicOperations.FixedTimeEquals`) where comparison time does not depend on the position of the first mismatched byte, eliminating timing-attack vulnerabilities.

### Timestamp Verification & Replay Window
Validation that the delivery timestamp supplied in provider headers is within an acceptable temporal tolerance window (e.g., ±5 minutes of the current system clock). Prevents adversaries from intercepting and replaying previously valid webhook transmissions.

### Timestamp Verifier (`IWebhookTimestampVerifier`)
A dedicated verification component that parses timestamp headers (Unix seconds, milliseconds, or ISO-8601) and validates temporal freshness against `IWebhookClock` within the provider's configured replay tolerance window.

### Clock Abstraction (`IWebhookClock`)
A deterministic time provider that abstracts `DateTimeOffset.UtcNow` across all validation, timestamp checks, and storage timestamps, enabling zero-flakiness automated time testing.

### Secret Rotation
The security practice of supporting multiple valid cryptographic secrets simultaneously (current secret and upcoming secret) during key changeover windows, preventing downtime during credential rotations.

### Payload Size Guard (`WebhookPayloadTooLargeException`)
A security mechanism in the body reader that monitors incoming byte stream lengths and aborts reading immediately if the payload exceeds `MaxRequestBodySizeBytes`, throwing `WebhookPayloadTooLargeException` to trigger an HTTP 413 (Payload Too Large) response before memory can be exhausted.

### Composite Extractor
An extractor strategy (`CompositeWebhookEventIdExtractor`, `CompositeWebhookEventTypeExtractor`) that chains multiple extraction mechanisms—attempting fast header extraction first, and falling back to JSON payload traversal when headers are absent.

---

## 3. Storage, State & Deduplication

### Deduplication Key
A composite unique key formatted as `{Provider}:{EventId}` that guarantees uniqueness across providers.

### Atomic Deduplication
A concurrency-safe check-and-insert operation executed against the persistence store (`IWebhookStore`) ensuring that even if 1,000 identical requests arrive simultaneously, exactly one request acquires processing rights while the other 999 are safely flagged as duplicates.

### Processing Status (`WebhookProcessingStatus`)
The lifecycle state of a webhook record:
- **`Received` (0)**: Initial ingress; signature and replay window verified, record created.
- **`Processing` (1)**: Worker or endpoint currently executing business handlers.
- **`Processed` (2)**: Application handler executed successfully to completion.
- **`Failed` (3)**: Processing encountered an unrecoverable exception or exceeded retry limits.
- **`Ignored` (4)**: Webhook was verified and recorded, but no registered handler matched its event type.
- **`Duplicate` (5)**: Event ID was previously recorded; duplicate response returned without re-executing handlers.

### Webhook Store (`IWebhookStore`)
The persistence abstraction responsible for atomically storing, querying, and transitioning webhook records across storage engines (In-Memory, Redis, Entity Framework Core).

---

## 4. Processing & Execution Models

### Synchronous Processing Mode
The HTTP request pipeline executes verification, deduplication, JSON deserialization, and the application's event handler in-process before returning the HTTP 200/202 response to the provider.

### Asynchronous (Queue + Worker) Mode
The HTTP request pipeline executes verification, deduplication, and persists the record to storage, enqueuing a reference into `IWebhookQueue` and immediately returning an HTTP 202 Accepted. An independent background worker (`WebhookBackgroundWorker`) dequeues records and invokes application handlers asynchronously.

### Handler Registry (`IWebhookHandler<T>`)
The dependency-injection-backed routing table mapping specific event types to strongly typed handler classes with full scoped DI lifetime support.

### Retry Engine
A fault-tolerance component applying exponential backoff and jitter to retry transient webhook handling failures up to a configured threshold (`MaxRetryAttempts`), distinguishing retryable network/database faults from non-retryable invalid payloads.
