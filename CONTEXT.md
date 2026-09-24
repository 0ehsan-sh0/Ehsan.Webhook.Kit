# WebhookKit Ubiquitous Language & Domain Glossary

This document serves as the single source of truth for ubiquitous language, domain concepts, and terminology across WebhookKit.

---

## 1. Core Concepts & Identity

### Webhook Record (`WebhookRecord`)
The persistent or in-flight representation of an ingested webhook delivery. It captures raw HTTP metadata (path, method, headers as read-only dictionary, content-type, content-length), the exact unmodified raw payload bytes (`byte[]? RawBody`) as source of truth, provider identification, delivery timestamps, processing state, attempt count, and diagnostic failure reasons.

### Webhook Context (`WebhookContext`)
The execution context passed into application event handlers. It provides read-only access to extracted metadata, headers, and typed payload access; raw body bytes remain inside the processing pipeline and are not exposed to business handlers.

### Webhook ID (`WebhookId`)
A ULID-formatted string identifier (`required string Id`, generated via `Guid.CreateVersion7`) created by WebhookKit at the instant of HTTP ingress. It uniquely identifies an *individual HTTP transmission* attempt from a provider.
_Avoid_: Event ID, correlation ID.

### Event ID (`EventId`)
The upstream provider's unique identifier for a business event (e.g., Stripe `evt_12345` or GitHub delivery GUID). Extracted from custom headers (e.g., `X-Event-Id`, `X-GitHub-Delivery`) or JSON payload properties (e.g., `id`, `event_id`). One Event ID may arrive across multiple Webhook IDs due to provider retries. It is required for the default deduplication policy; an explicit provider policy may use a deterministic body hash when no Event ID exists.

### Event Type (`EventType`)
The categorical classification of the webhook event (e.g., `payment.succeeded`, `customer.subscription.deleted`). Extracted from headers or payload bodies to route execution to registered event handlers (`IWebhookHandler<T>`).

---

## 2. Security, Integrity & Replay

### Raw Body Capture
The exact byte-for-byte replica (`byte[]`) of the inbound HTTP request stream preserved before any JSON deserialization or stream mutation occurs. It is essential because cryptographic signature verification fails if a single whitespace or line break is modified.
_Avoid_: decoded string body as source of truth.

### Signature Verification (`IWebhookSignatureVerifier`)
Cryptographic authentication of the payload confirming that the request originated from the authentic upstream provider. Typically computed using HMAC (HMAC-SHA256, HMAC-SHA512) over a provider-configurable signing input based on the raw request bytes and timestamp, using a shared secret.

### Verification Result (`WebhookVerificationResult`)
The outcome of signature verification carrying validity plus a non-sensitive failure reason for HTTP mapping. _Avoid_: throwing exceptions for expected rejections, leaking secrets in reasons.

### Verification Context (`WebhookVerificationContext`)
The inputs to signature verification: provider name, raw body bytes, and request headers. It carries no secrets.
_Avoid_: deserialized payload, string body.

### Constant-Time Comparison
A comparison routine (`CryptographicOperations.FixedTimeEquals`) where comparison time does not depend on the position of the first mismatched byte, eliminating timing-attack vulnerabilities.

### Timestamp Verification & Replay Window
Validation that the delivery timestamp supplied in provider headers is within an acceptable temporal tolerance window (e.g., ±5 minutes of the current system clock). Prevents adversaries from intercepting and replaying previously valid webhook transmissions. Providers without a usable timestamp must explicitly configure an alternative replay signal or opt out with a documented security tradeoff.

### Timestamp Verifier (`IWebhookTimestampVerifier`)
A dedicated verification component that parses timestamp headers (Unix seconds, milliseconds, or ISO-8601) and validates temporal freshness against `IWebhookClock` within the provider's configured replay tolerance window.

### Clock Abstraction (`IWebhookClock`)
A deterministic time provider that abstracts `DateTimeOffset.UtcNow` across all validation, timestamp checks, and storage timestamps, enabling zero-flakiness automated time testing.

### Secret Rotation
The security practice of supporting multiple valid cryptographic secrets simultaneously (current secret and upcoming secret) during key changeover windows, preventing downtime during credential rotations.

### Payload Size Guard (`WebhookPayloadTooLargeException`)
A security mechanism in the body reader that monitors incoming byte stream lengths and aborts reading immediately if the payload exceeds the effective global or provider-specific body limit, throwing `WebhookPayloadTooLargeException` to trigger an HTTP 413 (Payload Too Large) response before memory can be exhausted.

### Composite Extractor
An extractor strategy (`CompositeWebhookEventIdExtractor`, `CompositeWebhookEventTypeExtractor`) that chains multiple extraction mechanisms—attempting fast header extraction first, and falling back to JSON payload traversal when headers are absent.

---

## 3. Storage, State & Deduplication

### Deduplication Key
A provider-scoped identity key formed from the Event ID, or an explicitly configured deterministic body hash when the provider has no Event ID. It guarantees uniqueness only within the configured provider.

### Atomic Deduplication
A concurrency-safe claim operation executed against the persistence store (`IWebhookStore`) ensuring that even if many identical requests arrive simultaneously, exactly one delivery acquires processing rights while the others are safely identified as duplicates.

### Processing Status (`WebhookProcessingStatus`)
The lifecycle state of a webhook record:
- **`Received` (0)**: Initial ingress; signature and replay window verified, record created.
- **`Processing` (1)**: Worker or endpoint currently executing business handlers.
- **`Processed` (2)**: Application handler executed successfully to completion.
- **`Failed` (3)**: Processing encountered an unrecoverable exception or exceeded retry limits.
- **`Ignored` (4)**: Webhook was verified and recorded, but no registered handler matched its event type.
- **`Duplicate` (5)**: Event ID was previously recorded; duplicate response returned without re-executing handlers.

### Webhook Store (`IWebhookStore`)
The persistence abstraction responsible for atomically claiming, storing, querying, transitioning, and recovering webhook records across storage engines (In-Memory, Redis, Entity Framework Core). A processing lease coordinates ownership during asynchronous work.

---

## 4. Processing & Execution Models

### Synchronous Processing Mode
The default processing path. The HTTP request pipeline executes verification, deduplication, JSON deserialization, and the application's event handlers before returning the provider response.

### Asynchronous (Queue + Worker) Mode
An explicitly enabled processing path. The HTTP request pipeline verifies, deduplicates, persists, and enqueues a delivery reference; an independent worker invokes application handlers asynchronously. The initial queue is a bounded in-process notification channel and does not guarantee delivery across process restarts. When the queue is full, the delivery remains persisted and recoverable.

### Delivery Recovery
The process of reclaiming persisted deliveries that are waiting or have an expired processing lease, without re-executing completed deliveries.

### Handler Registry (`IWebhookHandler<T>`)
The dependency-injection-backed routing table mapping specific event types to strongly typed handler classes with full scoped DI lifetime support. Multiple matching handlers execute sequentially in registration order; the first failure stops the sequence.

### Ignored Event
A verified delivery with an event type that has no registered handler. It receives a provider-success response and is retained as `Ignored` without executing business handlers.

### Retry Engine
A fault-tolerance component applying exponential backoff and jitter to retry retryable webhook handling failures up to a configured total-attempt threshold, while treating invalid payloads, cancellation, and explicit permanent failures as non-retryable. Synchronous processing returns an error only after retry exhaustion.

### Payload Error
A verified delivery that cannot produce a valid typed payload or required event metadata. It is a non-retryable failure and is not exposed to application handlers as a partial payload.

### Provider Response
The HTTP response returned to an upstream provider. It acknowledges verified deliveries, duplicates, and ignored events while mapping rejected or failed deliveries to safe, configurable status codes and problem details. Synchronous acknowledgement is `200 OK`; asynchronous acknowledgement is `202 Accepted`.

### Safe Problem Response
The stable external error shape containing a safe machine code, human-readable message, and optional trace identifier. It never contains raw bodies, signatures, secrets, parser details, or exception messages.
