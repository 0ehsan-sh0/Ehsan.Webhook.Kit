# WebhookKit

## Production-Ready Webhook Infrastructure for ASP.NET Core

**Project name:** WebhookKit  
**Primary NuGet package:** `WebhookKit.AspNetCore`  
**Target framework:** .NET 8+  
**Language:** C#  
**Platform:** ASP.NET Core  
**License:** MIT  
**Repository:** `WebhookKit`

---

# 1. Project Overview

WebhookKit is a reusable, provider-agnostic webhook infrastructure library for ASP.NET Core.

It handles the infrastructure surrounding webhook delivery rather than the business logic of individual webhook events.

The library is designed to solve the common problems encountered when consuming webhooks:

- signature verification
- timestamp validation
- replay protection
- duplicate event detection
- idempotent processing
- raw request preservation
- webhook persistence
- processing state tracking
- background processing
- retry handling
- failure handling
- provider-specific verification
- extensibility
- observability
- structured logging
- correlation
- testability

The application developer should be able to create a production-grade webhook endpoint without implementing all of this infrastructure manually.

---

# 2. Problem Statement

A naive webhook endpoint often looks like:

```csharp
[HttpPost]
public async Task<IActionResult> Receive()
{
    var body = await new StreamReader(Request.Body).ReadToEndAsync();

    // Parse body
    // Validate signature
    // Check duplicate
    // Process event
    // Handle errors

    return Ok();
}
```

This becomes problematic in production.

A webhook provider may:

- send the same event multiple times
- retry after a timeout
- send requests out of order
- deliver a delayed event
- deliver an invalid signature
- replay an old valid request
- receive a slow response and retry
- send large payloads
- temporarily fail
- use different signature schemes

WebhookKit should provide the infrastructure required to handle these cases consistently.

---

# 3. Design Goals

## 3.1 Primary goals

WebhookKit must:

1. Be provider-agnostic.
2. Work with ASP.NET Core MVC.
3. Work with ASP.NET Core Minimal APIs.
4. Preserve the raw HTTP body for cryptographic verification.
5. Support synchronous and asynchronous processing models.
6. Support webhook deduplication.
7. Support replay protection.
8. Support pluggable persistence.
9. Support dependency injection.
10. Provide clear abstractions instead of tightly coupling every feature together.
11. Be easy to install.
12. Be easy to understand.
13. Be production-oriented.
14. Be highly testable.
15. Have no unnecessary dependencies.

---

# 4. Non-Goals

The first version must NOT become a giant framework.

Do not attempt to implement:

- a complete workflow engine
- a complete message broker
- a complete job scheduler
- a complete event bus
- every webhook provider
- business-domain event processing
- automatic retries of arbitrary business operations
- automatic database migrations
- a complete dashboard
- a hosted SaaS platform

WebhookKit provides infrastructure.

The consuming application owns the business logic.

---

# 5. Core Architecture

The high-level architecture is:

```text
                    External Provider
                           │
                           ▼
                  ┌─────────────────┐
                  │ HTTP Request    │
                  └────────┬────────┘
                           │
                           ▼
                  ┌─────────────────┐
                  │ Webhook Endpoint│
                  └────────┬────────┘
                           │
                           ▼
                  ┌─────────────────┐
                  │ Raw Body Reader │
                  └────────┬────────┘
                           │
                           ▼
                  ┌─────────────────┐
                  │ Signature       │
                  │ Verification    │
                  └────────┬────────┘
                           │
                           ▼
                  ┌─────────────────┐
                  │ Timestamp /     │
                  │ Replay Check    │
                  └────────┬────────┘
                           │
                           ▼
                  ┌─────────────────┐
                  │ Event Identity  │
                  │ Extraction      │
                  └────────┬────────┘
                           │
                           ▼
                  ┌─────────────────┐
                  │ Deduplication   │
                  └────────┬────────┘
                           │
                 ┌─────────┴─────────┐
                 │                   │
                 ▼                   ▼
          Synchronous            Persist + Queue
           Processing                 │
                 │                    ▼
                 │             Background Worker
                 │                    │
                 └─────────┬──────────┘
                           ▼
                  ┌─────────────────┐
                  │ Application     │
                  │ Event Handler   │
                  └────────┬────────┘
                           │
                           ▼
                  ┌─────────────────┐
                  │ Processing      │
                  │ Result          │
                  └─────────────────┘
```

---

# 6. Package Structure

The initial repository should use a multi-project solution.

```text
WebhookKit/
│
├── src/
│   ├── WebhookKit.Core/
│   ├── WebhookKit.AspNetCore/
│   ├── WebhookKit.Abstractions/
│   ├── WebhookKit.Infrastructure/
│   ├── WebhookKit.Redis/
│   ├── WebhookKit.EntityFrameworkCore/
│   └── WebhookKit.Testing/
│
├── tests/
│   ├── WebhookKit.Core.Tests/
│   ├── WebhookKit.AspNetCore.Tests/
│   ├── WebhookKit.Infrastructure.Tests/
│   ├── WebhookKit.Redis.Tests/
│   ├── WebhookKit.EntityFrameworkCore.Tests/
│   └── WebhookKit.IntegrationTests/
│
├── samples/
│   ├── MinimalApi/
│   ├── Mvc/
│   ├── Redis/
│   └── EntityFrameworkCore/
│
├── docs/
│
├── Directory.Build.props
├── Directory.Packages.props
├── README.md
├── LICENSE
├── WEBHOOKKIT.md
└── WebhookKit.sln
```

The architecture must keep the core functionality independent of infrastructure providers.

---

# 7. Package Responsibilities

## 7.1 WebhookKit.Abstractions

Contains interfaces and contracts.

Must NOT contain ASP.NET Core-specific code.

Examples:

```csharp
public interface IWebhookSignatureVerifier
{
    ValueTask<WebhookVerificationResult> VerifyAsync(
        WebhookVerificationContext context,
        CancellationToken cancellationToken = default);
}
```

```csharp
public interface IWebhookStore
{
    ValueTask<WebhookRecord?> GetAsync(
        string provider,
        string eventId,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryCreateAsync(
        WebhookRecord record,
        CancellationToken cancellationToken = default);

    ValueTask UpdateAsync(
        WebhookRecord record,
        CancellationToken cancellationToken = default);
}
```

```csharp
public interface IWebhookProcessor
{
    Task ProcessAsync(
        WebhookContext context,
        CancellationToken cancellationToken = default);
}
```

Other abstractions should include:

```text
IWebhookSignatureVerifier
IWebhookSignatureVerifierFactory
IWebhookStore
IWebhookDeduplicator
IWebhookProcessor
IWebhookHandler
IWebhookEventIdExtractor
IWebhookTimestampValidator
IWebhookDeserializer
IWebhookSerializer
IWebhookClock
IWebhookBodyReader
```

---

# 8. Core Domain Models

## 8.1 WebhookRecord

Represents a received webhook.

Suggested fields:

```csharp
public sealed class WebhookRecord
{
    public required string Id { get; init; }

    public required string Provider { get; init; }

    public string? EventId { get; init; }

    public string? EventType { get; init; }

    public required string HttpMethod { get; init; }

    public required string RequestPath { get; init; }

    public string? ContentType { get; init; }

    public long? ContentLength { get; init; }

    public string? RawBody { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }

    public DateTimeOffset? ProviderTimestamp { get; init; }

    public WebhookProcessingStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }

    public string? FailureReason { get; set; }
}
```

The exact model may change during implementation.

---

# 9. Processing Status

Create a strongly typed status enum.

```csharp
public enum WebhookProcessingStatus
{
    Received = 0,
    Processing = 1,
    Processed = 2,
    Failed = 3,
    Ignored = 4,
    Duplicate = 5
}
```

Do not expose magic integer values in public APIs without documenting them.

---

# 10. Verification

WebhookKit must treat signature verification as a first-class abstraction.

The library should support:

```text
HMAC-SHA256
HMAC-SHA512
```

initially.

The architecture must allow future algorithms without modifying the processing pipeline.

---

# 11. Signature Verification Model

A provider may send headers such as:

```http
X-Webhook-Signature: ...
X-Webhook-Timestamp: ...
X-Webhook-Event-Id: ...
```

The verification abstraction should not assume specific header names.

Example configuration:

```csharp
services.AddWebhookKit(options =>
{
    options.AddProvider("my-provider", provider =>
    {
        provider.Signature.HeaderName = "X-Webhook-Signature";
        provider.Signature.Algorithm = WebhookHashAlgorithm.HmacSha256;
        provider.Signature.Secret = "secret";
    });
});
```

Secrets should NOT be logged.

---

# 12. Timestamp Validation

Timestamp validation prevents replay of old but cryptographically valid requests.

Configuration:

```csharp
provider.Timestamp = new WebhookTimestampOptions
{
    HeaderName = "X-Webhook-Timestamp",
    Tolerance = TimeSpan.FromMinutes(5)
};
```

Validation should reject:

```text
timestamp too old
timestamp too far in the future
malformed timestamp
```

The clock must be abstracted:

```csharp
public interface IWebhookClock
{
    DateTimeOffset UtcNow { get; }
}
```

This makes tests deterministic.

---

# 13. Replay Protection

Replay protection is separate from signature verification.

A valid signature does NOT automatically mean that a request should be accepted.

WebhookKit should detect previously processed event identifiers or request signatures according to provider configuration.

Possible replay key:

```text
provider + eventId
```

or, where no event ID exists:

```text
provider + timestamp + bodyHash
```

The fallback strategy must be explicitly documented.

---

# 14. Deduplication

Webhook providers commonly retry the same event.

WebhookKit must guarantee that duplicate detection is performed atomically where the persistence implementation supports it.

Incorrect:

```csharp
if (!await store.ExistsAsync(key))
{
    await store.CreateAsync(key);
}
```

Two concurrent requests can both pass the check.

Preferred:

```csharp
var created = await store.TryCreateAsync(record);
```

The storage layer must perform the uniqueness operation atomically.

---

# 15. Idempotency

WebhookKit should distinguish:

### Duplicate delivery

The exact event has already been received.

### Idempotent processing

The processing operation can safely be invoked repeatedly.

WebhookKit can guarantee delivery deduplication where configured, but it must NOT falsely claim to guarantee arbitrary business-operation idempotency.

Documentation must clearly state this distinction.

---

# 16. Raw Body Handling

Raw request bytes are important because many signature algorithms sign the exact request payload.

Do NOT deserialize JSON before signature validation.

Correct sequence:

```text
HTTP request
     ↓
Read raw bytes
     ↓
Validate signature
     ↓
Validate timestamp
     ↓
Validate event identity
     ↓
Deserialize
```

The implementation must avoid accidental transformations of the payload before cryptographic verification.

---

# 17. Request Size Limits

WebhookKit must provide configurable maximum request size.

Example:

```csharp
options.MaxRequestBodySize = 1 * 1024 * 1024;
```

If the request exceeds the configured size:

```http
413 Payload Too Large
```

should be returned.

The implementation must avoid unnecessary memory allocations.

---

# 18. Provider Profiles

WebhookKit should support named provider configurations.

Example:

```csharp
builder.Services.AddWebhookKit(options =>
{
    options.AddProvider("stripe", provider =>
    {
        ...
    });

    options.AddProvider("github", provider =>
    {
        ...
    });

    options.AddProvider("internal", provider =>
    {
        ...
    });
});
```

Provider configuration should include:

```text
Name
Signature configuration
Timestamp configuration
Event ID extraction
Event type extraction
Secret
Replay policy
Deserializer
Processing configuration
```

---

# 19. Webhook Endpoint API

The library should provide a clean API for Minimal APIs.

Example:

```csharp
app.MapWebhook(
    "/webhooks/payment",
    async (WebhookContext context, CancellationToken ct) =>
    {
        await handler.HandleAsync(context, ct);
    });
```

Potential provider-specific overload:

```csharp
app.MapWebhook(
    "/webhooks/payment",
    provider: "payment-provider",
    async context =>
    {
        ...
    });
```

The API must remain simple.

---

# 20. MVC Support

WebhookKit must support MVC controllers.

Example:

```csharp
[ApiController]
[Route("webhooks")]
public class WebhooksController : ControllerBase
{
    [HttpPost("payment")]
    [Webhook("payment-provider")]
    public async Task<IActionResult> Receive(
        WebhookContext context,
        CancellationToken cancellationToken)
    {
        ...
    }
}
```

The final attribute/API design can be adjusted during implementation, but MVC and Minimal APIs must both be first-class supported scenarios.

---

# 21. Handler Model

Developers should have the option of strongly typed handlers.

Example:

```csharp
public interface IWebhookHandler<TEvent>
{
    Task HandleAsync(
        TEvent eventData,
        WebhookContext context,
        CancellationToken cancellationToken);
}
```

Example:

```csharp
public sealed class PaymentCompletedHandler
    : IWebhookHandler<PaymentCompleted>
{
    public Task HandleAsync(
        PaymentCompleted eventData,
        WebhookContext context,
        CancellationToken cancellationToken)
    {
        ...
    }
}
```

---

# 22. Event Type Routing

A provider may send:

```json
{
  "id": "evt_123",
  "type": "payment.completed",
  "data": { }
}
```

WebhookKit should allow handlers to be selected by event type.

Example:

```csharp
services.AddWebhookHandler<PaymentCompletedHandler>(
    "payment.completed");
```

The library should not require developers to manually write:

```csharp
switch (event.Type)
{
    ...
}
```

for common strongly typed scenarios.

---

# 23. Serialization

Use `System.Text.Json` by default.

Do not introduce Newtonsoft.Json unless there is a compelling requirement.

Serialization must be configurable.

Example:

```csharp
options.JsonSerializerOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};
```

---

# 24. Processing Modes

WebhookKit should support two processing modes.

## 24.1 Synchronous

```text
Receive
  ↓
Validate
  ↓
Process
  ↓
Return response
```

Useful for simple applications.

## 24.2 Asynchronous

```text
Receive
  ↓
Validate
  ↓
Persist
  ↓
Queue
  ↓
Return success
  ↓
Background processor
```

Recommended for production workloads where webhook providers have strict timeout limits.

---

# 25. Queue Abstraction

Do NOT tightly couple the core package to RabbitMQ.

Create:

```csharp
public interface IWebhookQueue
{
    ValueTask EnqueueAsync(
        string webhookId,
        CancellationToken cancellationToken = default);
}
```

A basic implementation may use an in-process background queue.

Future packages may support:

```text
Redis
RabbitMQ
Azure Service Bus
Amazon SQS
Kafka
```

These integrations should be separate packages.

---

# 26. Background Processing

Provide an optional hosted service.

Example:

```csharp
services.AddWebhookKit(options =>
{
    options.Processing.Mode =
        WebhookProcessingMode.Background;
});
```

The worker must:

- retrieve pending records
- claim work safely
- process events
- update status
- handle cancellation
- record failures
- increment attempts
- avoid concurrent duplicate processing

---

# 27. Retry Policy

Retries must be configurable.

Example:

```csharp
options.Retry = new WebhookRetryOptions
{
    MaxAttempts = 5,
    InitialDelay = TimeSpan.FromSeconds(5),
    BackoffMultiplier = 2
};
```

Retry strategy:

```text
Attempt 1
   ↓
5 sec
   ↓
Attempt 2
   ↓
10 sec
   ↓
Attempt 3
   ↓
20 sec
   ↓
Attempt 4
```

The retry system must support jitter to avoid synchronized retries in distributed systems.

---

# 28. Permanent Failures

Not every exception should result in infinite retries.

After the retry limit is reached:

```text
Status = Failed
```

The failure must contain:

```text
Failure reason
Attempt count
Last attempted timestamp
Exception type
```

Sensitive information must not be exposed in HTTP responses.

---

# 29. Dead-Letter Support

The first release does not need to implement a complete dead-letter broker.

However, the domain model and interfaces must allow a failed webhook to be moved into a dead-letter state later.

Possible future status:

```csharp
DeadLettered
```

The design must not prevent this extension.

---

# 30. Response Behavior

WebhookKit should define predictable HTTP responses.

Example:

### Accepted

```http
200 OK
```

or:

```http
202 Accepted
```

### Invalid signature

```http
401 Unauthorized
```

### Replay / stale timestamp

```http
401 Unauthorized
```

or another clearly documented response.

### Duplicate webhook

Normally:

```http
200 OK
```

A duplicate should generally not cause provider retries.

### Payload too large

```http
413 Payload Too Large
```

### Unsupported event

The behavior must be configurable.

---

# 31. Security Requirements

Security is a primary concern.

WebhookKit must:

- compare signatures using constant-time comparison
- never log webhook secrets
- never log authorization credentials
- never expose secrets in exception messages
- validate timestamps
- protect against replay
- enforce payload size limits
- avoid unsafe reflection-based behavior
- avoid dynamic code execution
- avoid insecure default secret storage
- avoid disabling signature validation by default

---

# 32. Secret Configuration

Never require developers to put secrets directly in source code in documentation examples except clearly labeled development examples.

Recommended:

```csharp
var secret = configuration["WebhookKit:Providers:Stripe:Secret"];
```

or:

```csharp
provider.Secret = configuration["WEBHOOK_SECRET"];
```

The library should integrate naturally with ASP.NET Core configuration.

---

# 33. Observability

WebhookKit should support structured logging using `ILogger`.

Useful event fields:

```text
WebhookId
Provider
EventId
EventType
ProcessingStatus
Attempt
ElapsedMilliseconds
```

Never log:

```text
Secret
Authorization header
Full sensitive payload
Sensitive personal information
```

unless explicitly enabled by the application.

---

# 34. Metrics

Metrics should be exposed through an abstraction rather than forcing a specific telemetry framework.

Useful metrics:

```text
webhook.received
webhook.verified
webhook.rejected
webhook.duplicate
webhook.processed
webhook.failed
webhook.retry
webhook.processing_duration
```

OpenTelemetry integration should be a future package or optional integration.

---

# 35. Tracing

The project should use `ActivitySource` where appropriate.

Suggested activity:

```text
WebhookKit.Receive
WebhookKit.Process
```

Tags:

```text
webhook.provider
webhook.event_id
webhook.event_type
webhook.status
```

Do not attach sensitive payloads to spans.

---

# 36. Storage Abstraction

Core WebhookKit must not require a database.

Default storage:

```text
InMemoryWebhookStore
```

This is primarily useful for:

- local development
- testing
- simple applications

It must be explicitly documented that in-memory storage is unsuitable for multi-instance production deployments.

---

# 37. Redis Storage

Create:

```text
WebhookKit.Redis
```

It should provide a Redis-backed implementation of:

```csharp
IWebhookStore
IWebhookDeduplicator
```

Redis operations must use atomic primitives where required.

Avoid race conditions such as:

```text
GET
then
SET
```

when the operation requires atomic uniqueness.

---

# 38. Entity Framework Core Storage

Create:

```text
WebhookKit.EntityFrameworkCore
```

Support:

```text
SQL Server
PostgreSQL
other relational EF Core providers
```

Do not make the package dependent on a specific database engine.

The consuming application should own its DbContext.

Example:

```csharp
services.AddWebhookKitEntityFrameworkCore<AppDbContext>();
```

---

# 39. Database Requirements

The EF Core implementation should store:

```text
Webhook ID
Provider
Event ID
Event Type
Status
Raw Body
Content Type
Received At
Provider Timestamp
Attempt Count
Processed At
Failed At
Failure Reason
```

Create a unique index where applicable:

```text
Provider + EventId
```

The implementation must handle duplicate insert races correctly.

---

# 40. Sensitive Data

Raw webhook payloads can contain personal or confidential information.

Therefore:

- payload persistence must be configurable
- payload retention must be configurable
- logging payloads must be disabled by default
- developers must be able to disable raw-body storage
- documentation must warn users about privacy implications

Future versions may support payload encryption.

---

# 41. Retention

Provide configuration such as:

```csharp
options.Storage.RetentionPeriod =
    TimeSpan.FromDays(30);
```

Retention itself may initially be delegated to the storage provider.

Do not create a complex cleanup scheduler in the first release unless it is necessary.

---

# 42. Extensibility

Everything provider-specific should be implemented behind interfaces.

Examples:

```text
IWebhookSignatureVerifier
IWebhookEventIdExtractor
IWebhookEventTypeExtractor
IWebhookDeserializer
IWebhookStore
IWebhookQueue
```

A developer should be able to implement their own provider without modifying WebhookKit source code.

---

# 43. Provider Adapter Architecture

Provider support should be optional.

Example:

```text
WebhookKit
    │
    ├── Generic HMAC
    ├── Timestamp validation
    └── Event routing

WebhookKit.Stripe
    │
    └── Stripe-specific signature implementation

WebhookKit.GitHub
    │
    └── GitHub-specific implementation
```

Do not put provider-specific logic into the core package.

---

# 44. Dependency Injection

WebhookKit must integrate cleanly with Microsoft.Extensions.DependencyInjection.

Public registration:

```csharp
services.AddWebhookKit();
```

Provider registration:

```csharp
services.AddWebhookKit(options =>
{
    options.AddProvider(...);
});
```

Custom implementation:

```csharp
services.AddSingleton<IWebhookStore, CustomWebhookStore>();
```

The package must not replace or manipulate the application's service provider.

---

# 45. Configuration

Configuration objects should be strongly typed.

Example:

```csharp
public sealed class WebhookKitOptions
{
    public long MaxRequestBodySize { get; set; }

    public WebhookProcessingOptions Processing { get; set; }

    public WebhookRetryOptions Retry { get; set; }

    public WebhookStorageOptions Storage { get; set; }
}
```

Use options validation where appropriate.

Invalid configuration should fail fast during startup when possible.

---

# 46. Error Handling

The library must define internal exception types only where they provide meaningful value.

Potential exceptions:

```text
WebhookVerificationException
WebhookReplayException
WebhookPayloadTooLargeException
WebhookDeserializationException
WebhookProcessingException
WebhookConfigurationException
```

Exceptions should not leak sensitive information.

---

# 47. Cancellation

Every asynchronous public API must support:

```csharp
CancellationToken
```

The implementation must correctly propagate cancellation.

Do not swallow:

```csharp
OperationCanceledException
```

without a good reason.

---

# 48. Thread Safety

All infrastructure implementations intended for DI singleton use must be thread-safe.

Especially:

```text
InMemoryWebhookStore
WebhookDeduplicator
Configuration caches
Provider registry
```

Tests must explicitly verify concurrent access.

---

# 49. Performance Requirements

WebhookKit should avoid unnecessary allocations.

Important paths:

```text
request body reading
signature calculation
duplicate checking
JSON deserialization
```

Do not prematurely optimize at the expense of correctness.

Performance benchmarks should be added after the basic implementation works.

---

# 50. Public API Quality

Public APIs must be:

- minimal
- documented
- nullable-safe
- asynchronous where appropriate
- cancellation-aware
- versionable
- backward-compatible

Avoid exposing internal implementation details.

---

# 51. Nullable Reference Types

Enable:

```xml
<Nullable>enable</Nullable>
```

The project must have no unnecessary nullable warnings.

---

# 52. Analyzers

Enable standard .NET analyzers.

Prefer:

```xml
<AnalysisLevel>latest</AnalysisLevel>
```

where supported by the selected target framework and project configuration.

Warnings should be treated seriously.

---

# 53. Testing Strategy

Testing is a first-class requirement.

Test layers:

```text
Unit tests
Integration tests
Concurrency tests
Security tests
Serialization tests
End-to-end tests
```

---

# 54. Unit Tests

Test:

### Signature verification

- valid signature
- invalid signature
- malformed signature
- wrong algorithm
- wrong secret
- constant-time comparison behavior

### Timestamp validation

- valid timestamp
- expired timestamp
- future timestamp
- malformed timestamp
- boundary conditions

### Deduplication

- first event accepted
- second event rejected as duplicate
- concurrent duplicate requests

### Event routing

- matching event type
- unknown event type
- malformed event

### Serialization

- valid JSON
- malformed JSON
- null values
- property casing

---

# 55. Integration Tests

Integration tests should use:

```csharp
WebApplicationFactory<TEntryPoint>
```

and verify actual HTTP behavior.

Tests should include:

```text
POST webhook
signature validation
status code
headers
event handler invocation
duplicate behavior
payload size behavior
```

---

# 56. Redis Integration Tests

Use a disposable Redis instance or test container.

Test:

```text
concurrent deduplication
TTL behavior
storage retrieval
processing status updates
```

---

# 57. EF Core Integration Tests

Use Testcontainers or an appropriate real database test environment.

Do not rely exclusively on EF Core InMemory provider for relational behavior.

Test:

```text
unique constraints
concurrent insertion
transactions
status updates
```

---

# 58. Concurrency Tests

This is especially important.

Simulate:

```text
100 concurrent requests
same provider
same event ID
```

Expected result:

```text
1 request = accepted/created
99 requests = duplicates
```

The exact result should be based on the configured behavior, but there must never be 100 independent event records for the same unique event.

---

# 59. Security Tests

Security tests must cover:

```text
timing-safe signature comparison
replay attacks
invalid signature
missing signature
oversized requests
malformed timestamp
malformed payload
secret not included in logs
```

---

# 60. Samples

Provide at least four sample applications.

## Sample 1 — Minimal API

```text
samples/MinimalApi
```

Demonstrates the simplest possible setup.

## Sample 2 — MVC

```text
samples/Mvc
```

Demonstrates controller integration.

## Sample 3 — Redis

```text
samples/Redis
```

Demonstrates production-style deduplication/storage.

## Sample 4 — EF Core

```text
samples/EntityFrameworkCore
```

Demonstrates persisted webhook records.

Every sample must be runnable by following the README.

---

# 61. Documentation

README must contain:

1. What WebhookKit is.
2. Why it exists.
3. Installation.
4. Quick start.
5. Minimal API example.
6. MVC example.
7. HMAC example.
8. Provider configuration.
9. Duplicate handling.
10. Background processing.
11. Redis setup.
12. EF Core setup.
13. Security considerations.
14. Production recommendations.
15. Extension guide.
16. Architecture overview.
17. Contributing instructions.
18. License.

---

# 62. Quick Start Documentation

The ideal first experience should be approximately:

```bash
dotnet add package WebhookKit.AspNetCore
```

Then:

```csharp
builder.Services.AddWebhookKit();

var app = builder.Build();

app.MapWebhook("/webhooks/example", async context =>
{
    Console.WriteLine(context.EventId);
});

app.Run();
```

The API should be understandable without reading the architecture documentation.

---

# 63. Example Production Configuration

A production example should look approximately like:

```csharp
builder.Services.AddWebhookKit(options =>
{
    options.MaxRequestBodySize = 1024 * 1024;

    options.Processing.Mode =
        WebhookProcessingMode.Background;

    options.Retry.MaxAttempts = 5;

    options.AddProvider("payments", provider =>
    {
        provider.Signature.HeaderName =
            "X-Webhook-Signature";

        provider.Signature.Algorithm =
            WebhookHashAlgorithm.HmacSha256;

        provider.Signature.Secret =
            builder.Configuration["Webhooks:Payments:Secret"];

        provider.Timestamp.HeaderName =
            "X-Webhook-Timestamp";

        provider.Timestamp.Tolerance =
            TimeSpan.FromMinutes(5);

        provider.EventId.HeaderName =
            "X-Webhook-Id";
    });
});
```

This exact API may be refined during implementation.

---

# 64. Versioning Strategy

Use Semantic Versioning.

```text
MAJOR.MINOR.PATCH
```

Examples:

```text
1.0.0
1.1.0
1.1.1
2.0.0
```

Breaking public API changes require a major version.

---

# 65. Package Naming

Recommended:

```text
WebhookKit.Abstractions
WebhookKit.Core
WebhookKit.AspNetCore
WebhookKit.Redis
WebhookKit.EntityFrameworkCore
WebhookKit.Testing
```

Do not publish unnecessary packages before they are useful.

The initial NuGet release should likely focus on:

```text
WebhookKit.AspNetCore
WebhookKit.Abstractions
WebhookKit.Core
```

---

# 66. API Compatibility

Public API changes must be reviewed carefully.

Avoid changing public interfaces unnecessarily.

Prefer additive API evolution.

---

# 67. CI/CD

GitHub Actions should perform:

```text
restore
build
test
pack
```

On pull requests:

```text
dotnet restore
dotnet build
dotnet test
```

On release tags:

```text
build
test
pack
publish NuGet
```

NuGet publishing should use GitHub Actions secrets.

Never commit API keys.

---

# 68. Code Quality

The project must follow standard C# conventions.

Use:

```text
PascalCase
camelCase
file-scoped namespaces
async/await
nullable reference types
sealed classes where appropriate
readonly where appropriate
```

Avoid:

```text
God classes
static global state
service locator
reflection without need
hidden network calls
magic strings
```

---

# 69. Logging Rules

Default logging should be informative but safe.

Example:

```text
Webhook received.
Provider=payments
EventId=evt_123
WebhookId=01J...
```

Do NOT log:

```text
Secret=...
Authorization=...
RawBody=...
```

unless an explicitly configured unsafe/debug mode exists.

Even then, documentation must warn users.

---

# 70. Correlation

WebhookKit should create or reuse a correlation identifier.

Example:

```text
X-Correlation-ID
```

The correlation ID should flow into:

```text
logs
Activity
processing context
```

Do not confuse correlation IDs with event IDs.

---

# 71. Event ID vs Webhook ID

These are separate concepts.

### Webhook ID

Generated by WebhookKit.

Example:

```text
01K7ABC...
```

### Event ID

Provided by the external provider.

Example:

```text
evt_123456
```

Both should be preserved.

---

# 72. Id Generation

WebhookKit IDs should use an appropriate unique identifier.

A future-friendly option is UUIDv7 when the target runtime supports it appropriately.

Do not expose internal implementation assumptions unnecessarily.

---

# 73. Event Type

Event type should be treated as an opaque string.

Examples:

```text
payment.completed
invoice.created
user.updated
order.shipped
```

The library should not impose an enum for provider event types.

---

# 74. Multiple Providers

An application may consume many providers.

Example:

```text
POST /webhooks/stripe
POST /webhooks/github
POST /webhooks/payment
POST /webhooks/internal
```

Each endpoint may have a separate configuration.

The underlying infrastructure should be shared safely.

---

# 75. Multiple Secrets

Provider secret rotation should be possible.

A provider may temporarily accept:

```text
current secret
previous secret
```

Architecture should allow multiple active signing secrets without requiring changes to the core pipeline.

---

# 76. Secret Rotation

Future API:

```csharp
provider.Signature.Secrets.Add(
    configuration["Webhook:CurrentSecret"]);

provider.Signature.Secrets.Add(
    configuration["Webhook:PreviousSecret"]);
```

The exact API can be designed later.

---

# 77. Testing Utilities

`WebhookKit.Testing` may provide:

```csharp
WebhookRequestBuilder
WebhookSignatureBuilder
WebhookTestServer
WebhookPayloadFactory
```

Example:

```csharp
var request = WebhookRequestBuilder
    .Create("payment.completed")
    .WithEventId("evt_123")
    .WithPayload(payload)
    .Sign(secret);
```

This makes integration testing dramatically easier.

---

# 78. Benchmarking

Create a benchmark project later:

```text
benchmarks/WebhookKit.Benchmarks
```

Benchmark:

```text
signature verification
body hashing
deduplication lookup
serialization
request processing
```

Do not optimize blindly based on theoretical concerns.

---

# 79. Dependency Philosophy

Keep dependencies minimal.

Core should depend on as little as possible.

Do not bring:

```text
Redis
EF Core
RabbitMQ
Newtonsoft.Json
```

into the core package merely because optional integrations exist.

Use separate packages.

---

# 80. Initial MVP Scope

The MVP must include:

```text
✓ ASP.NET Core integration
✓ Minimal APIs
✓ MVC
✓ raw-body access
✓ HMAC-SHA256
✓ HMAC-SHA512
✓ timestamp validation
✓ replay protection
✓ event ID extraction
✓ event type extraction
✓ deduplication abstraction
✓ in-memory store
✓ structured logging
✓ request-size limit
✓ JSON deserialization
✓ strongly typed handlers
✓ unit tests
✓ integration tests
✓ samples
✓ documentation
```

The MVP should NOT require Redis or EF Core.

---

# 81. Post-MVP

After the MVP:

```text
Redis support
EF Core support
background processing
retry policies
testing package
provider adapters
OpenTelemetry integration
```

---

# 82. Future Features

Potential future roadmap:

```text
WebhookKit.Redis
WebhookKit.EntityFrameworkCore
WebhookKit.RabbitMQ
WebhookKit.Kafka
WebhookKit.AzureServiceBus

WebhookKit.Stripe
WebhookKit.GitHub
WebhookKit.PayPal

Webhook dashboard
Webhook replay API
Webhook inspection UI
payload encryption
automatic cleanup
dead-letter storage
advanced retry policies
tenant isolation
multi-tenant provider configuration
```

These must not complicate the core MVP.

---

# 83. Repository Layout

Final expected layout:

```text
WebhookKit/
│
├── src/
│   ├── WebhookKit.Abstractions/
│   ├── WebhookKit.Core/
│   ├── WebhookKit.AspNetCore/
│   ├── WebhookKit.Redis/
│   ├── WebhookKit.EntityFrameworkCore/
│   └── WebhookKit.Testing/
│
├── tests/
│   ├── WebhookKit.Abstractions.Tests/
│   ├── WebhookKit.Core.Tests/
│   ├── WebhookKit.AspNetCore.Tests/
│   ├── WebhookKit.Redis.Tests/
│   ├── WebhookKit.EntityFrameworkCore.Tests/
│   └── WebhookKit.IntegrationTests/
│
├── samples/
│   ├── MinimalApi/
│   ├── Mvc/
│   ├── Redis/
│   └── EntityFrameworkCore/
│
├── benchmarks/
│
├── docs/
│
├── .github/
│   └── workflows/
│
├── Directory.Build.props
├── Directory.Packages.props
├── WebhookKit.sln
├── README.md
├── WEBHOOKKIT.md
├── LICENSE
└── .gitignore
```

---

# 84. Implementation Tasks

Implementation must be performed in the following order.

Tasks should be completed sequentially unless a task explicitly states otherwise.

---

# Task 01 — Repository Bootstrap

## Objective

Create the complete .NET solution structure.

## Include

- solution file
- source projects
- test projects
- sample projects
- benchmark project
- common build configuration
- package version management
- `.gitignore`
- license
- initial README

## Requirements

- Target .NET 8+
- Enable nullable reference types
- Enable analyzers
- Use consistent package versions
- Configure CI-ready builds

## Done when

```bash
dotnet build
```

succeeds for the complete solution.

---

# Task 02 — Core Domain and Abstractions

## Objective

Create the provider-neutral contracts.

## Implement

- `WebhookRecord`
- `WebhookContext`
- `WebhookProcessingStatus`
- `WebhookVerificationResult`
- `WebhookVerificationContext`
- `IWebhookStore`
- `IWebhookDeduplicator`
- `IWebhookProcessor`
- `IWebhookHandler<T>`
- `IWebhookSignatureVerifier`
- `IWebhookEventIdExtractor`
- `IWebhookEventTypeExtractor`
- `IWebhookClock`
- relevant options/contracts

## Rules

No ASP.NET Core dependency inside `WebhookKit.Abstractions`.

## Done when

All contracts compile and have focused unit tests where applicable.

---

# Task 03 — Configuration System

## Objective

Create the options/configuration infrastructure.

## Implement

- `WebhookKitOptions`
- `WebhookProviderOptions`
- `WebhookSignatureOptions`
- `WebhookTimestampOptions`
- `WebhookRetryOptions`
- processing options
- storage options
- request limits
- provider registry

## Include

- sensible defaults
- validation
- clear error messages

## Done when

Invalid configuration fails predictably and valid configuration can be resolved through DI.

---

# Task 04 — Clock Abstraction

## Objective

Create deterministic time handling.

## Implement

```csharp
IWebhookClock
SystemWebhookClock
```

## Requirements

All timestamp-sensitive logic should use the abstraction.

## Tests

- current time
- fake clock
- exact tolerance boundary

---

# Task 05 — Raw HTTP Body Reader

## Objective

Implement reliable raw request-body capture.

## Requirements

- preserve bytes exactly
- support signature verification
- support configurable maximum body size
- avoid unnecessary copies
- handle empty bodies
- support cancellation

## Tests

- valid body
- empty body
- large body
- oversized body
- malformed request
- cancellation

---

# Task 06 — HMAC Signature Engine

## Objective

Implement generic cryptographic signature verification.

## Implement

- HMAC-SHA256
- HMAC-SHA512
- constant-time comparison
- hex encoding support
- Base64 support where configured

## Tests

- valid signature
- invalid signature
- wrong secret
- malformed signature
- encoding mismatch
- empty body
- Unicode body

Never log secrets or signatures unnecessarily.

---

# Task 07 — Timestamp Verification

## Objective

Implement replay-window validation.

## Requirements

Support:

```text
header extraction
Unix timestamps
DateTimeOffset timestamps
configurable tolerance
clock abstraction
```

## Tests

- valid timestamp
- expired request
- future request
- malformed request
- tolerance boundaries

---

# Task 08 — Event ID Extraction

## Objective

Create provider-neutral event ID extraction.

## Sources may include

- HTTP header
- JSON property
- custom extractor

## Implement

```text
HeaderEventIdExtractor
JsonEventIdExtractor
CompositeEventIdExtractor
```

The exact classes may be simplified if unnecessary.

---

# Task 09 — Event Type Extraction

## Objective

Provide event-type routing.

Support:

```json
{
  "type": "payment.completed"
}
```

and custom providers.

## Tests

- valid type
- missing type
- malformed payload
- custom property

---

# Task 10 — In-Memory Store

## Objective

Provide a development/test storage implementation.

## Requirements

- thread safe
- atomic creation
- lookup
- update
- status transitions

## Critical test

Run many concurrent requests for the same event.

Only one must successfully create the unique event.

---

# Task 11 — Deduplication Pipeline

## Objective

Integrate event identification and storage.

## Processing

```text
Receive
  ↓
Identify provider
  ↓
Extract event ID
  ↓
Build deduplication key
  ↓
Atomic create
  ↓
Duplicate?
```

## Behavior

If duplicate:

```text
do not process again
return configured success response
```

---

# Task 12 — Webhook Context

## Objective

Create the object supplied to application handlers.

It should provide access to:

```text
WebhookId
Provider
EventId
EventType
RawBody
Request metadata
ReceivedAt
ProviderTimestamp
Headers
Processing metadata
```

Sensitive values should not accidentally be exposed through logging/to-string methods.

---

# Task 13 — JSON Deserialization

## Objective

Deserialize webhook payloads after verification.

## Requirements

- System.Text.Json
- configurable options
- cancellation support where applicable
- useful error handling

Example:

```csharp
var payload =
    context.GetPayload<PaymentCompleted>();
```

---

# Task 14 — Handler Registry

## Objective

Route event types to strongly typed handlers.

Example:

```csharp
services.AddWebhookHandler<PaymentCompletedHandler>(
    "payment.completed");
```

## Requirements

- DI integration
- multiple handlers
- unknown event behavior
- scoped dependency support

---

# Task 15 — Minimal API Integration

## Objective

Provide the primary endpoint API.

Example target API:

```csharp
app.MapWebhook(
    "/webhooks/payment",
    "payments",
    async context =>
    {
        ...
    });
```

API syntax can be refined during implementation.

## Tests

Use real `WebApplicationFactory`.

---

# Task 16 — MVC Integration

## Objective

Provide equivalent support for MVC/controller applications.

Implement:

- endpoint metadata
- filter/attribute or endpoint integration
- provider selection
- context injection

Ensure the MVC API is not substantially harder than Minimal API usage.

---

# Task 17 — HTTP Response Model

## Objective

Standardize verification and processing responses.

Implement configurable handling for:

```text
success
duplicate
invalid signature
replay
invalid payload
unsupported event
oversized request
processing failure
```

Document all defaults.

---

# Task 18 — Logging

## Objective

Implement structured logging.

Add useful logs for:

```text
received
verified
rejected
duplicate
processing
processed
failed
retry
```

Use structured logging fields.

Never log secrets.

---

# Task 19 — Correlation and Activity

## Objective

Add observability foundations.

Implement:

```text
ActivitySource
correlation ID
processing activity
```

Do not depend on OpenTelemetry packages.

---

# Task 20 — Background Queue Abstraction

## Objective

Create an abstraction for asynchronous processing.

Implement:

```csharp
IWebhookQueue
```

Provide an in-process implementation.

Do not add RabbitMQ yet.

---

# Task 21 — Background Worker

## Objective

Implement hosted processing.

Requirements:

- retrieve pending work
- process
- update status
- handle cancellation
- recover after failures
- prevent duplicate concurrent processing

---

# Task 22 — Retry Engine

## Objective

Implement controlled retries.

Include:

```text
maximum attempts
exponential backoff
jitter
retryable/non-retryable failure
```

The retry system must not create unbounded background work.

---

# Task 23 — Redis Integration

## Objective

Implement the optional Redis package.

Package:

```text
WebhookKit.Redis
```

Include:

- Redis store
- distributed deduplication
- expiration/TTL
- atomic operations

Test concurrent access.

---

# Task 24 — EF Core Integration

## Objective

Implement relational persistence.

Package:

```text
WebhookKit.EntityFrameworkCore
```

Include:

- entities/configuration
- index configuration
- optimistic/concurrent safety where applicable
- atomic event creation

The consuming application should control its DbContext.

---

# Task 25 — Testing Package

## Objective

Create:

```text
WebhookKit.Testing
```

Include convenient helpers for:

```text
request creation
signature generation
fake webhook events
integration testing
```

---

# Task 26 — Security Audit

## Objective

Perform a focused security review.

Verify:

```text
constant-time comparison
replay prevention
body size limits
secret handling
logging
header handling
request parsing
exception leakage
concurrency
```

No release before this task is complete.

---

# Task 27 — Concurrency Audit

## Objective

Stress-test duplicate handling.

Run:

```text
10 concurrent requests
100 concurrent requests
1000 concurrent requests
```

for the same event ID.

Verify there is exactly one logical accepted event.

Repeat against:

```text
Memory
Redis
EF Core
```

where supported.

---

# Task 28 — Integration Test Suite

## Objective

Create complete end-to-end scenarios.

At minimum:

```text
valid webhook
invalid signature
expired timestamp
duplicate event
unknown event
malformed JSON
oversized payload
processing exception
successful retry
retry exhaustion
background processing
```

---

# Task 29 — Samples

## Objective

Make the project immediately understandable.

Implement:

```text
MinimalApi
Mvc
Redis
EntityFrameworkCore
```

Each should contain:

- README
- configuration instructions
- runnable example
- sample webhook payload

---

# Task 30 — Documentation

## Objective

Write production-quality documentation.

README sections:

```text
Overview
Features
Installation
Quick start
Minimal API
MVC
Providers
Verification
Deduplication
Background processing
Storage
Redis
EF Core
Security
Configuration
Testing
Extension points
Architecture
FAQ
Contributing
License
```

---

# Task 31 — Package Metadata

## Objective

Prepare NuGet packages properly.

Include:

```text
PackageId
Version
Authors
Description
PackageTags
RepositoryUrl
PackageReadmeFile
LicenseExpression
Symbol package
SourceLink
```

Use XML documentation generation.

---

# Task 32 — NuGet Package Validation

## Objective

Build and inspect actual `.nupkg` and `.snupkg` files.

Verify:

- correct assemblies
- XML docs
- README
- symbols
- dependency list
- no accidental source files
- no development secrets
- no unnecessary dependencies

---

# Task 33 — CI/CD

## Objective

Create GitHub Actions.

PR pipeline:

```text
restore
build
test
pack
```

Release pipeline:

```text
version
build
test
pack
publish NuGet
```

Never hardcode credentials.

---

# Task 34 — API Review

## Objective

Before version 1.0, review every public type.

Check:

```text
Naming
Nullability
Cancellation
Default behavior
Breaking change risk
Documentation
Exception behavior
Thread safety
```

Remove APIs that are unnecessary.

---

# Task 35 — Benchmarking

## Objective

Create benchmark measurements.

Benchmark:

```text
HMAC verification
deduplication
serialization
request processing
```

Record baseline results.

Do not optimize solely for benchmark scores.

---

# Task 36 — v1.0 Release Candidate

## Objective

Freeze the public API and prepare release candidate.

Checklist:

```text
All tests passing
No analyzer warnings
No known security issues
Samples working
Documentation complete
NuGet package validated
README complete
License included
CI green
```

---

# Task 37 — Version 1.0 Release

Release:

```text
WebhookKit.Abstractions
WebhookKit.Core
WebhookKit.AspNetCore
```

Only publish additional integration packages when they are genuinely production-ready.

---

# 85. Recommended Implementation Order

The coding agent should follow this dependency order:

```text
01 Repository
       ↓
02 Abstractions
       ↓
03 Configuration
       ↓
04 Clock
       ↓
05 Raw Body
       ↓
06 HMAC
       ↓
07 Timestamp
       ↓
08 Event ID
       ↓
09 Event Type
       ↓
10 Memory Store
       ↓
11 Deduplication
       ↓
12 Context
       ↓
13 Serialization
       ↓
14 Handler Registry
       ↓
15 Minimal API
       ↓
16 MVC
       ↓
17 HTTP Responses
       ↓
18 Logging
       ↓
19 Observability
       ↓
20 Queue
       ↓
21 Worker
       ↓
22 Retry
       ↓
23 Redis
       ↓
24 EF Core
       ↓
25 Testing Package
       ↓
26 Security Audit
       ↓
27 Concurrency Audit
       ↓
28 Integration Tests
       ↓
29 Samples
       ↓
30 Documentation
       ↓
31 Package Metadata
       ↓
32 NuGet Validation
       ↓
33 CI/CD
       ↓
34 API Review
       ↓
35 Benchmark
       ↓
36 Release Candidate
       ↓
37 v1.0
```

---

# 86. Definition of Done

A task is complete only when:

1. Code is implemented.
2. Public APIs have XML documentation where appropriate.
3. Unit tests exist for non-trivial behavior.
4. Existing tests still pass.
5. No new analyzer warnings are introduced.
6. Cancellation is handled correctly.
7. Error behavior is explicit.
8. Security implications are considered.
9. The implementation does not unnecessarily couple packages.
10. The code follows the architecture described in this document.

---

# 87. Agent Rules

When implementing this project with an AI coding agent, follow these rules.

## Rule 1

Do not implement all tasks in one giant change.

Complete one task at a time.

## Rule 2

Before implementing a task, inspect the existing repository and current architecture.

Never overwrite working code merely to match a theoretical design.

## Rule 3

Do not introduce dependencies without justification.

## Rule 4

Do not weaken security to make tests pass.

## Rule 5

Do not hide failures with broad exception handling.

Avoid:

```csharp
catch (Exception)
{
}
```

## Rule 6

Do not create public APIs prematurely.

Prefer internal implementations until the API is clearly required.

## Rule 7

Every concurrency-sensitive operation must have a concurrency test.

## Rule 8

All cryptographic operations must have dedicated tests.

## Rule 9

Do not log sensitive payloads or secrets.

## Rule 10

Do not claim a business operation is idempotent merely because webhook delivery is deduplicated.

---

# 88. Design Principles

WebhookKit must follow these architectural principles:

### Simple by default

The basic use case should require minimal configuration.

### Secure by default

Unsafe behavior must never be the default.

### Provider agnostic

Provider-specific logic belongs in adapters.

### Infrastructure independent

Core abstractions must not require Redis, EF Core, RabbitMQ, or a specific database.

### Explicit behavior

Developers must understand what happens when:

- verification fails
- a duplicate arrives
- processing fails
- a retry occurs

### Observable

Production systems must be able to understand webhook processing behavior.

### Testable

Time, storage, verification, queueing and processing must be replaceable.

---

# 89. Example End-State

The final developer experience should look close to this:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWebhookKit(options =>
{
    options.AddProvider("payments", provider =>
    {
        provider.Signature.HeaderName = "X-Signature";
        provider.Signature.Algorithm =
            WebhookHashAlgorithm.HmacSha256;

        provider.Signature.Secret =
            builder.Configuration["Webhooks:Payments:Secret"];

        provider.Timestamp.HeaderName =
            "X-Timestamp";

        provider.Timestamp.Tolerance =
            TimeSpan.FromMinutes(5);

        provider.EventId.HeaderName =
            "X-Event-Id";
    });
});

builder.Services.AddWebhookHandler<
    PaymentCompletedHandler>("payment.completed");

var app = builder.Build();

app.MapWebhook(
    "/webhooks/payments",
    "payments");

app.Run();
```

The underlying framework should automatically perform:

```text
HTTP request
    ↓
body size check
    ↓
raw body capture
    ↓
signature verification
    ↓
timestamp validation
    ↓
replay protection
    ↓
event ID extraction
    ↓
duplicate detection
    ↓
event type extraction
    ↓
JSON deserialization
    ↓
handler resolution
    ↓
processing
    ↓
status update
    ↓
logging / activity
```

---

# 90. Long-Term Product Vision

WebhookKit should eventually become a small ecosystem for reliable webhook infrastructure.

Potential architecture:

```text
                         WebhookKit
                             │
              ┌──────────────┼──────────────┐
              │              │              │
             Core         ASP.NET Core    Testing
              │
       ┌──────┼───────────┬───────────┐
       │      │           │           │
     Redis   EF Core   RabbitMQ     Kafka
       │
       │
       ▼
   Production
 Infrastructure
```

Provider adapters can then live independently:

```text
WebhookKit.Stripe
WebhookKit.GitHub
WebhookKit.PayPal
WebhookKit.Custom
```

The goal is not to become a monolithic framework.

The goal is to become the reusable **infrastructure layer developers reach for when building serious webhook consumers in .NET**.

---

# 91. Final MVP Success Criteria

Version 1.0 should satisfy all of the following:

```text
✓ One-command NuGet installation
✓ Minimal API support
✓ MVC support
✓ Generic HMAC verification
✓ Replay protection
✓ Atomic deduplication
✓ Strongly typed event handlers
✓ Raw body preservation
✓ Configurable payload limits
✓ In-memory storage
✓ Background processing
✓ Retry handling
✓ Structured logging
✓ Activity/correlation support
✓ Extensive tests
✓ Concurrency-safe implementation
✓ Security-focused implementation
✓ Redis integration
✓ EF Core integration
✓ Working samples
✓ Complete documentation
✓ CI/CD
✓ Valid NuGet packages
```

The resulting package must be useful without Redis or a database, while still providing a clean path to production-grade distributed deployments.