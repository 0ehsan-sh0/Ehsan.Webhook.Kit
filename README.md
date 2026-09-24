# WebhookKit

WebhookKit is provider-agnostic webhook infrastructure for ASP.NET Core. It verifies the exact request bytes, checks provider timestamps, deduplicates deliveries atomically, dispatches typed handlers, and persists processing state for synchronous or explicitly asynchronous endpoints.

The repository currently delivers six packages targeting `net8.0`, `net9.0`, and `net10.0`. Samples and tests target `net8.0`.

## Overview

WebhookKit keeps provider-specific configuration at the edge and gives application code a small, typed boundary:

- HMAC-SHA256 and HMAC-SHA512 verification with fixed-time byte comparison and secret rotation.
- Required-by-default timestamp freshness checks with an explicit missing-timestamp policy.
- Header-first, JSON-fallback, and custom DI extraction for Event ID and Event Type.
- Atomic store-backed deduplication and provider-scoped identity.
- Synchronous processing by default, with an explicit asynchronous queue-and-worker mode.
- In-memory, Redis, and application-owned Entity Framework Core store options.
- Safe HTTP acknowledgements and problem responses that never echo payloads, signatures, secrets, parser details, or exception messages.
- `WebhookKit.Testing` helpers for deterministic clocks, signed requests, and host harnesses.

Start with the [configuration reference](Docs/configuration.md) for the complete option surface, the [verification notes](Docs/verification.md) for tested behavior and evidence, and the [security audit](Docs/security-audit.md) before deploying.

## Install packages

A Minimal API or MVC application normally installs the ASP.NET Core package, which brings in the core and abstractions projects:

```powershell
dotnet add package WebhookKit.AspNetCore
```

Install an optional provider only when the application uses it:

```powershell
dotnet add package WebhookKit.Redis
dotnet add package WebhookKit.EntityFrameworkCore
dotnet add package WebhookKit.Testing
```

| Package | Use |
| --- | --- |
| `WebhookKit.Abstractions` | Provider-neutral contracts, records, status values, and handler interfaces. |
| `WebhookKit.Core` | Options, HMAC and timestamp verification, extraction, in-memory store, queue, worker, and retry engine. |
| `WebhookKit.AspNetCore` | Minimal API and MVC integration, raw body reading, and response writing. |
| `WebhookKit.Redis` | Redis-backed `IWebhookStore` with atomic scripts and independent TTLs. |
| `WebhookKit.EntityFrameworkCore` | EF Core `IWebhookStore` and `ApplyWebhookConfiguration` model mapping. |
| `WebhookKit.Testing` | Fake clock, signed request builder, signature generator, and test harness. |

Repository samples use project references so they always exercise the delivered source projects. The runnable projects are [Minimal API](samples/MinimalApi), [MVC](samples/Mvc), [Redis](samples/Redis), and [EF Core](samples/EntityFrameworkCore).

## Minimal API first run

Place this in `Program.cs` of an ASP.NET Core Web SDK project. The secret is read from configuration, which can be populated by `WEBHOOKKIT_PROVIDER_SECRET`; it is never embedded in source.

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore.DependencyInjection;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Options;

var builder = WebApplication.CreateBuilder(args);
var secret = builder.Configuration["WEBHOOKKIT_PROVIDER_SECRET"];
if (string.IsNullOrWhiteSpace(secret))
{
    throw new InvalidOperationException("Set WEBHOOKKIT_PROVIDER_SECRET before starting.");
}

builder.Services.AddWebhookKit(options =>
{
    options.AddProvider("payments", provider =>
    {
        provider.Signature.HeaderName = "X-Webhook-Signature";
        provider.Signature.Algorithm = WebhookHashAlgorithm.HmacSha256;
        provider.Signature.Encoding = WebhookSignatureEncoding.Hex;
        provider.Signature.Input = WebhookSignatureInput.RawBody;
        provider.Signature.Secret = secret;
        provider.Timestamp.HeaderName = "X-Webhook-Timestamp";
        provider.Timestamp.Tolerance = TimeSpan.FromMinutes(5);
        provider.EventIdHeaderName = "X-Webhook-Event-Id";
        provider.EventTypeHeaderName = "X-Webhook-Event-Type";
    });
});
builder.Services.AddWebhookKitAspNetCore();
builder.Services.AddWebhookHandler<PaymentCompletedHandler>("payment.completed");

var app = builder.Build();
app.MapWebhook("/webhooks/payments", "payments");
app.Run();
return;

public sealed record PaymentCompleted(string PaymentId, decimal Amount);

public sealed class PaymentCompletedHandler : IWebhookHandler<PaymentCompleted>
{
    public Task HandleAsync(
        PaymentCompleted eventData,
        WebhookContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
```

The provider must send the configured Event ID, Event Type, timestamp, and signature headers. A valid request for the first-run profile has this shape:

```http
POST /webhooks/payments HTTP/1.1
Content-Type: application/json
X-Webhook-Event-Id: evt_123
X-Webhook-Event-Type: payment.completed
X-Webhook-Timestamp: <current Unix seconds>
X-Webhook-Signature: <lowercase hexadecimal HMAC-SHA256>

{"paymentId":"pay_123","amount":1250}
```

For the `RawBody` input shown above, the signing input is the exact UTF-8 body bytes. The provider uses the configured shared secret as the UTF-8 HMAC key, computes HMAC-SHA256, and sends lowercase hexadecimal. The timestamp is still verified independently. The sample request files show how to generate a current timestamp and signature immediately before sending; never reserialize the JSON after it has been signed.

The first correctly signed synchronous request returns `200 OK` with an empty acknowledgement. A duplicate also returns `200 OK` without running the handler again. An unknown Event Type is also acknowledged and retained as `Ignored`; a missing Event Type is a `400` payload error.

## Handler example

The first-run block already contains a complete handler. The handler contract is:

```csharp
public interface IWebhookHandler<in TEvent>
{
    Task HandleAsync(
        TEvent eventData,
        WebhookContext context,
        CancellationToken cancellationToken = default);
}
```

Register a concrete handler with `AddWebhookHandler<THandler>(eventType)`. WebhookKit resolves the handler from the current scope, deserializes `TEvent` with the configured `System.Text.Json` options, caches one successful payload per type in the context, and invokes matching handlers sequentially in registration order. The first handler failure stops the sequence.

Use `context.Provider`, `context.EventId`, `context.EventType`, `context.WebhookId`, `context.CorrelationId`, `context.ReceivedAt`, `context.ProviderTimestamp`, and `context.Headers` for safe metadata. `ProviderTimestamp` is the exact parsed provider timestamp when timestamp verification produced one. `Headers` is a detached `IReadOnlyDictionary<string, IReadOnlyList<string>>`; `CorrelationId` is non-null and receives a stable, atomically published generated fallback when no caller or ambient value is available. Use `context.GetPayload<T>()` inside application code. The context does not expose a public raw-body property; the pipeline owns raw bytes.

## Configuration

A provider profile is added with `AddWebhookKit` and validated at startup. Names are case-insensitive and duplicate or empty names fail fast.

```csharp
builder.Services.AddWebhookKit(options =>
{
    options.MaxRequestBodySizeBytes = 1024 * 1024;
    options.Storage.PersistRawBody = true;
    options.Storage.DiscardRawBodyAfterSuccessfulSync = true;
    options.Queue.Capacity = 1024;
    options.Background.Enabled = false;
    options.Background.ShutdownDrainTimeout = TimeSpan.FromSeconds(5);

    options.AddProvider("payments", provider =>
    {
        provider.MaxRequestBodySizeBytes = 256 * 1024;
        provider.Signature.HeaderName = "X-Webhook-Signature";
        provider.Signature.Algorithm = WebhookHashAlgorithm.HmacSha256;
        provider.Signature.Encoding = WebhookSignatureEncoding.Hex;
        provider.Signature.Input = WebhookSignatureInput.RawBody;
        provider.Signature.Secret = builder.Configuration["Payments:Secret"];
        var previousSecret = builder.Configuration["Payments:PreviousSecret"];
        if (!string.IsNullOrWhiteSpace(previousSecret))
        {
            provider.Signature.AdditionalSecrets.Add(previousSecret);
        }
        provider.Timestamp.HeaderName = "X-Webhook-Timestamp";
        provider.Timestamp.Tolerance = TimeSpan.FromMinutes(5);
        provider.EventIdHeaderName = "X-Webhook-Event-Id";
        provider.EventTypeHeaderName = "X-Webhook-Event-Type";
        provider.Retry.MaxAttempts = 3;
        provider.Retry.InitialDelay = TimeSpan.FromSeconds(2);
        provider.Retry.BackoffMultiplier = 2;
        provider.Retry.UseJitter = true;
        provider.Retry.JitterRatio = 0.2;
    });
});
```

`MaxAttempts` includes the initial attempt. The delivered defaults are three total attempts, a two-second initial delay, multiplier `2`, jitter enabled, and a `0.2` jitter ratio. A handler can explicitly throw `WebhookRetryableException` to request another attempt or `WebhookPermanentException` to stop immediately; `WebhookPayloadException`, cancellation, and unknown exceptions are terminal.

The important profile settings are:

| Setting | Behavior |
| --- | --- |
| `Signature.HeaderName` | Enables signature verification when configured with a secret. |
| `Signature.Algorithm` | `HmacSha256` (default) or `HmacSha512`. |
| `Signature.Encoding` | `Hex` (default) or `Base64`. Hex comparison accepts either case. |
| `Signature.Input` | `RawBody` (default) or `TimestampPrefixedRawBody`. |
| `Signature.TimestampSeparator` | Separator used by timestamp-prefixed input; defaults to `.`. |
| `Signature.Secret` and `AdditionalSecrets` | Current and temporary rotation secrets. Never log them. |
| `Timestamp.HeaderName` | Required timestamp header unless `Timestamp.AllowMissing` is explicitly true. |
| `Timestamp.Tolerance` | Allowed clock skew in either direction; defaults to five minutes. |
| `EventIdHeaderName` | Header-first Event ID extractor name. |
| `EventTypeHeaderName` | Header-first Event Type extractor name. |
| `AllowBodyHashFallback` | Explicitly permits a deterministic body-hash key when no Event ID is available; defaults to false. |
| `MaxRequestBodySizeBytes` | Per-provider body limit; otherwise the global limit is used. |

`RawBody` signs only the body bytes. `TimestampPrefixedRawBody` signs the UTF-8 timestamp bytes, `TimestampSeparator`, and then the exact body bytes, so it requires a timestamp header. The verifier parses Unix seconds, Unix milliseconds, or ISO-8601 timestamps and rejects a value whose absolute skew is greater than the configured tolerance. On success, the exact parsed value is available as `WebhookVerificationResult.ProviderTimestamp` and is propagated to `WebhookRecord.ProviderTimestamp` and `WebhookContext.ProviderTimestamp`. A missing timestamp is rejected by default; `AllowMissing = true` is an explicit security tradeoff and does not make a malformed timestamp valid.

The default global body limit is `1024 * 1024` bytes. The reader checks an advertised `Content-Length` first and bounds streamed reads to the limit plus one proof byte. Exceeding either global or provider limit returns `413 Payload Too Large` before the oversized body is retained.

Raw body and header ownership are separate concerns:

- The body reader captures the exact bytes before deserialization and rewinds the ASP.NET request stream for downstream consumers.
- `Storage.PersistRawBody` defaults to `true`. When false, the store receives a record without `RawBody`, while the current synchronous context can still use its in-memory copy.
- `Storage.DiscardRawBodyAfterSuccessfulSync` defaults to `false`. When true, a successfully processed or ignored synchronous record is updated to remove its persisted raw body. Asynchronous processing requires raw-body persistence.
- Captured headers are copied into detached, read-only snapshots and remain part of the persisted record by default. Treat headers and raw bodies as sensitive application data, not diagnostic text.

JSON uses `System.Text.Json`. `WebhookKitOptions.JsonSerializerOptions` defaults to `PropertyNameCaseInsensitive = true`; configure naming policies or other `JsonSerializerOptions` values on that property. Malformed JSON, an empty result, and invalid typed payloads become a non-retryable payload failure.

The full option and endpoint reference is in [Docs/configuration.md](Docs/configuration.md).

## Provider extraction

The default extractor chain is header-first with JSON fallback:

1. Read the configured Event ID header.
2. If no value is present, read the JSON `id` property.
3. Do the same for Event Type using the configured header and JSON `type` property.
4. Require a non-empty Event Type before deduplication and handler dispatch.

JSON extraction supports dotted object paths such as `data.object.id` and `data.object.type`. The default JSON paths are `id` and `type`; configure a different path or provider-specific logic through DI. Register a replacement before `AddWebhookKit`, or remove the default registration and add the replacement after it:

```csharp
using Microsoft.Extensions.DependencyInjection;
using WebhookKit.Abstractions;
using WebhookKit.Core.Extractors;

builder.Services.AddSingleton<IWebhookEventIdExtractor>(
    new JsonEventIdExtractor("data.id"));
builder.Services.AddSingleton<IWebhookEventTypeExtractor>(
    new JsonEventTypeExtractor("data.type"));
builder.Services.AddWebhookKit(options =>
{
    options.AddProvider("payments", provider =>
    {
        provider.Signature.HeaderName = "X-Webhook-Signature";
        provider.Signature.Secret = builder.Configuration["Payments:Secret"];
        provider.Timestamp.HeaderName = "X-Webhook-Timestamp";
    });
});
```

A custom extractor receives the provider name, header snapshot, and exact raw bytes through `WebhookVerificationContext`, returns `null` when it cannot find a value, and should honor cancellation. If header-first fallback is still required, compose the built-in `HeaderEventIdExtractor` or `HeaderEventTypeExtractor` with the JSON extractor in a custom `IWebhookEventIdExtractor` or `IWebhookEventTypeExtractor` registration.

A verified Event Type with no registered handler is a successful, visible `Ignored` outcome. A missing Event Type is a `400` failure and is never passed to a handler. An Event ID is required by default; enabling `AllowBodyHashFallback` is an explicit alternative for providers that do not send one.

## MVC

MVC uses the same ingestion service, response writer, deduplication, verification, and status behavior as Minimal API. Register MVC and the shared ASP.NET Core services:

```csharp
builder.Services.AddControllers();
builder.Services.AddWebhookKit(options =>
{
    options.AddProvider("payments", provider =>
    {
        provider.Signature.HeaderName = "X-Webhook-Signature";
        provider.Signature.Secret = builder.Configuration["Payments:Secret"];
        provider.Timestamp.HeaderName = "X-Webhook-Timestamp";
    });
});
builder.Services.AddWebhookKitAspNetCore();
builder.Services.AddWebhookHandler<PaymentCompletedHandler>("payment.completed");
```

Use the delivered attribute on the action:

```csharp
using Microsoft.AspNetCore.Mvc;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore.Mvc;

[ApiController]
[Route("webhooks/payments")]
public sealed class PaymentsController : ControllerBase
{
    [HttpPost]
    [WebhookEndpoint("payments")]
    public IActionResult Receive(WebhookContext context)
    {
        _ = context.GetPayload<PaymentCompleted>();
        return Ok();
    }
}
```

`AddWebhookKitAspNetCore` installs the shared filter and model binder. The default MVC action policy is `WebhookEndpointActionPolicy.Processed`, so the action runs for a processed synchronous delivery and is short-circuited for duplicates, ignored deliveries, and failures. Set `ActionPolicy` deliberately when an MVC action must also run for other successful outcomes. The action owns application behavior only; it does not reimplement verification or deduplication.

## Synchronous and asynchronous modes

The one-line `MapWebhook(path, providerName)` overload is synchronous and returns `200 OK` after processing. It is the default and requires no queue configuration.

Asynchronous mode is explicit. Enable the hosted worker and select the asynchronous endpoint mode:

```csharp
using WebhookKit.AspNetCore.Pipeline;
using WebhookKit.Core.Options;

builder.Services.AddWebhookKit(options =>
{
    options.Background.Enabled = true;
    options.Background.WorkerConcurrency = 1;
    options.Background.ShutdownDrainTimeout = TimeSpan.FromSeconds(5);
    options.AddProvider("payments", provider =>
    {
        provider.Signature.HeaderName = "X-Webhook-Signature";
        provider.Signature.Secret = builder.Configuration["Payments:Secret"];
        provider.Timestamp.HeaderName = "X-Webhook-Timestamp";
    });
});

var app = builder.Build();
app.MapWebhook("/webhooks/payments", new WebhookEndpointOptions("payments")
{
    Mode = WebhookProcessingMode.Asynchronous
});
app.Run();
```

Asynchronous admission verifies the request, atomically persists the record, and attempts to enqueue a `WebhookWorkItem` before returning `202 Accepted`. The default queue capacity is `1024`; a full or unavailable queue returns `503 Service Unavailable` while the persisted record remains recoverable.

The default `IWebhookQueue` is a bounded, in-process notification channel. It is not durable across a process restart and Redis persistence does not make this channel durable. The `IWebhookStore` is the authority for records, deduplication, claims, leases, terminal state, and recovery. The default worker periodically queries recoverable `Received` and expired-lease `Processing` records. Default recovery settings are a 30-second interval, 100-record batch, two-minute lease duration, 30-second recovery age, and a five-second shutdown processor drain timeout. Terminal records are never reclaimed.

A lease is a time-bounded owner token. Claims atomically move an eligible record to `Processing`, install the owner and expiry, and increment `AttemptCount`. A stale owner cannot release, complete, fail, or overwrite a newer owner. Synchronous and worker terminal acknowledgements are emitted only after the store confirms the owned transition; ignored records are also re-read to confirm `Ignored` status and cleared lease ownership. Cancellation releases a claim when possible. During shutdown, the worker waits for active processor tasks for at most `ShutdownDrainTimeout`; an uncooperative handler produces only the safe `shutdown-drain-timeout` diagnostic and does not hold shutdown indefinitely. Configure lease duration above the maximum retry window for every provider; startup validation enforces that relationship.

## In-memory storage

`AddWebhookKit` registers an internal in-memory `IWebhookStore` implementation by default. It is concurrency-safe within one process and is useful for local development, tests, and single-process applications. It is not a shared or durable store: records, deduplication markers, leases, and recovery state disappear on process restart, and separate application instances do not coordinate. Do not use it for multi-instance production deduplication.

The in-memory store still follows the configured raw-body policy and captures request headers. Use Redis or an application-owned relational store for persistence that must survive restarts or coordinate instances.

## Redis

Add `WebhookKit.Redis` and register the store after the core registration:

```csharp
var redisConnection = builder.Configuration["WEBHOOKKIT_REDIS_CONNECTION"];
if (string.IsNullOrWhiteSpace(redisConnection))
{
    throw new InvalidOperationException("Set WEBHOOKKIT_REDIS_CONNECTION before starting.");
}

builder.Services.AddWebhookKitRedis(redisConnection, options =>
{
    options.KeyPrefix = "payments-webhooks";
    options.DeduplicationRetention = TimeSpan.FromDays(7);
    options.RecordRetention = TimeSpan.FromDays(30);
});
```

The package also accepts an already-created `IConnectionMultiplexer`. The string overload registers lazy connection creation. Key prefixes are limited to safe ASCII letters, digits, dots, underscores, and hyphens, so use `payments-webhooks`, not a colon-delimited name.

Redis creation atomically writes the deduplication marker and full record. The defaults are exactly seven days for deduplication retention and thirty days for full-record retention; state transitions preserve the record TTL independently. Redis keys hash the provider-scoped identity rather than exposing provider IDs or body hashes in key names.

**Redis runtime evidence:** the committed standalone local Redis gate ran successfully against a disposable Redis 7.4.5 service, including all live store rows, 10/100/1,000-way concurrency checks, stale-owner protection, TTL behavior, and the Redis sample `202/202/401` matrix. This is not production, Redis Cluster, or deployment-infrastructure validation: cluster slot co-location, managed-service identity/TLS/network policy, failover, capacity, backup, monitoring, and organizational controls remain deployment gates. `abortConnect=true` still proves lazy startup wiring only and does not make a live request valid.

## Entity Framework Core

Add `WebhookKit.EntityFrameworkCore` and the EF provider used by the application (for example, `Microsoft.EntityFrameworkCore.Sqlite`), register the application-owned context, and map the package entity:

```csharp
using Microsoft.EntityFrameworkCore;
using WebhookKit.EntityFrameworkCore;

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration["ConnectionStrings:Webhooks"]));
builder.Services.AddWebhookKit(options =>
{
    options.AddProvider("payments", provider =>
    {
        provider.Signature.HeaderName = "X-Webhook-Signature";
        provider.Signature.Secret = builder.Configuration["Payments:Secret"];
        provider.Timestamp.HeaderName = "X-Webhook-Timestamp";
    });
});
builder.Services.AddWebhookKitAspNetCore();
builder.Services.AddWebhookKitEntityFrameworkCore<AppDbContext>();
```

```csharp
using Microsoft.EntityFrameworkCore;
using WebhookKit.EntityFrameworkCore;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyWebhookConfiguration();
    }
}
```

`AddWebhookKitEntityFrameworkCore<TContext>` registers a scoped store and does not register the application `DbContext`, choose a database provider, create a schema, or ship migrations. The application owns the context, connection, schema, migration files, deployment order, and backup/retention policy. The sample uses SQLite and `EnsureCreated` only because it is a runnable demonstration; production applications should use their normal migration workflow.

The model has a unique deduplication key, owner-protected processing leases, recovery indexes, raw-body bytes, serialized headers, attempt timestamps, and safe failure codes. SQLite is the executed relational authority in this workspace; other relational providers are supported through provider-neutral EF mappings but were not live-tested here.

## Testing helpers

Install `WebhookKit.Testing` in a test project. It provides deterministic time, request construction, signature generation, and host lifecycle helpers:

```csharp
using WebhookKit.Testing;

var secret = Environment.GetEnvironmentVariable("TEST_WEBHOOK_SECRET")
    ?? throw new InvalidOperationException("Set TEST_WEBHOOK_SECRET for this test.");
var clock = new FakeWebhookClock(
    new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
var requestBuilder = WebhookTestRequestBuilder
    .Create("payments", clock)
    .WithEventId("evt_test_123")
    .WithEventType("payment.completed")
    .WithJson(new { paymentId = "pay_test_123", amount = 1250 });
var generator = new WebhookSignatureGenerator(secret);
using var signedRequest = requestBuilder.BuildSigned(generator);
```

`WebhookTestRequestBuilder` copies builder state and raw bytes, supports header names, timestamp formats, JSON, raw bodies, and custom header names, and returns a new `HttpRequestMessage` for each build. `WebhookSignatureGenerator` supports HMAC-SHA256/SHA512, hex/base64, raw-body and timestamp-prefixed inputs, rotation verification, and custom signing-input callbacks. Generation uses the primary secret; verification checks the primary and configured rotation secrets.

`WebhookTestHarness` can start a supplied `IWebhookTestHost` or client factory, create clients, send requests, stop, and dispose cleanly. `WebhookTestHarness<TApplication>` is the convenience wrapper over `WebApplicationFactory<TApplication>`; the application remains responsible for its configuration, services, and test credentials.

See the [testing package source and tests](src/WebhookKit.Testing) for the complete helper surface.

## Response codes and safe errors

The following are the default endpoint mappings. Endpoint options can override individual status codes or provide a `StatusCodeSelector`; the table describes the delivered defaults.

| Outcome | Sync | Async | Default problem `Code` | Default `Message` |
| --- | ---: | ---: | --- | --- |
| Processed | `200` | `202` | `processed` | `Webhook processed.` |
| Accepted for worker | n/a | `202` | `accepted` | `Webhook accepted.` |
| Duplicate | `200` | `202` | `duplicate` | `Webhook already received.` |
| Unknown Event Type (`Ignored`) | `200` | `202` | `ignored` | `Webhook ignored.` |
| Missing Event ID | `400` | `400` | `event-id-required` | `Webhook event identifier is required.` |
| Missing Event Type | `400` | `400` | `missing-event-type` | `Webhook event type is required.` |
| Invalid payload or extraction | `400` | `400` | `payload-invalid` | `Webhook payload is invalid.` |
| Invalid or stale timestamp | `400` | `400` | `timestamp-verification-failed` | `Webhook timestamp verification failed.` |
| Invalid or missing signature | `401` | `401` | `signature-verification-failed` | `Webhook signature verification failed.` |
| Body over configured limit | `413` | `413` | `payload-too-large` | `Webhook payload is too large.` |
| Processing/configuration failure | `500` | `500` or prior `202` | `handler-failed` or `webhook-configuration-error` | `Webhook processing failed.` or `Webhook processing is not configured.` |
| Queue unavailable or full | n/a | `503` | `queue-unavailable` | `Webhook processing is temporarily unavailable.` |

Successful `200` and `202` acknowledgements have no response body. Error responses use `application/problem+json` and the safe shape:

```json
{
  "code": "signature-verification-failed",
  "message": "Webhook signature verification failed.",
  "traceId": "safe-trace-identifier"
}
```

`TraceId` is optional and is taken from the current `Activity` or the ASP.NET request trace identifier. It never contains a secret, raw body, signature, parser detail, or exception message. Unknown exceptions are terminal by default; only explicit retryable failures are retried. In asynchronous mode, retry exhaustion is recorded as `Failed` after the original `202` acknowledgement.

## Security

Read [Docs/security-audit.md](Docs/security-audit.md) before production use. The important boundary is:

- The raw body is captured before deserialization and remains inside the ingestion/processing boundary; handlers receive metadata, headers, and typed payloads rather than a public raw-body property.
- HMAC comparison uses `CryptographicOperations.FixedTimeEquals` for every configured primary and rotation secret before returning a generic result; secrets, signatures, payloads, authorization headers, parser details, and exception messages are not logged or returned by the delivered safe surfaces.
- Timestamp freshness and body-size limits are enforced before handler execution.
- Redis and EF records can persist raw bodies and captured headers, including sensitive values. Encrypt, restrict, redact, and retain them according to application policy.
- The in-process asynchronous queue is not durable across restarts. A persisted record can be recovered, but a lost notification is not a durable broker guarantee.
- The committed standalone local Redis gate passed, but GitHub-hosted CI, production/cluster/infrastructure validation, and external release configuration remain pending. The local gate is not external certification.
- The audit is an internal source and executable-test review, not an independent penetration test or security certification.

Never put a production secret in source, a committed `.env` file, a request file, a log example, or a diagnostic payload. Use configuration providers, environment variables, or a secret manager.

## Troubleshooting

| Symptom | Likely cause | Check |
| --- | --- | --- |
| Startup configuration error | Missing secret, missing required timestamp header, invalid queue/background values, or asynchronous raw-body policy | Verify the environment variable name and the provider profile; do not print its value. |
| `401` signature failure | Wrong secret, wrong raw bytes, wrong encoding, unsupported prefix, or signature not generated from the exact body | Sign the body once, send those exact bytes, and use the configured hex/base64 format. |
| `400` timestamp failure | Timestamp is outside the five-minute tolerance, malformed, or missing without `AllowMissing` | Generate a current Unix-seconds value immediately before sending. |
| `400` missing Event ID | The configured header and JSON fallback found no Event ID | Configure an extractor or explicitly enable the body-hash fallback after reviewing its identity tradeoff. |
| `400` missing Event Type versus ignored | Missing metadata is malformed; a present unregistered type is acknowledged and ignored | Check the configured type header/JSON path and handler registration. |
| `400` payload invalid | JSON is malformed, empty, null, or incompatible with configured `JsonSerializerOptions` | Validate the exact body and configure naming/case options on `WebhookKitOptions.JsonSerializerOptions`. |
| `413` payload too large | Global or provider body limit exceeded | Increase the applicable limit only after reviewing memory and provider constraints. |
| `503` queue unavailable | Asynchronous queue is full, closed, or not registered | Check `Queue.Capacity`, `Background.Enabled`, worker startup, and persisted recovery. The record is not discarded. |
| `500` after retries | Explicit retryable failures exhausted the attempt limit, or a dependency/handler failed | Use the safe `Code` and `TraceId`; inspect protected application logs without logging payload data. Unknown exceptions are terminal. |
| Redis connection or command failure | No reachable Redis service or invalid connection settings | Start a real service, verify `WEBHOOKKIT_REDIS_CONNECTION`, and run the live Redis checks; `abortConnect=true` is startup-only. |
| EF schema or unique-key failure | `ApplyWebhookConfiguration` was not called or the application schema is out of date | Apply the model configuration, create/migrate the application schema, and inspect the unique deduplication key. |
| Duplicate is acknowledged but handler did not run | Atomic deduplication or MVC action policy short-circuited the delivery | This is expected; inspect the store record and `WebhookEndpointActionPolicy`. |

## Building, testing, and formatting

Run these commands from the repository root in PowerShell:

```powershell
dotnet restore "Ehsan.Webhook.Kit.slnx"
dotnet build "Ehsan.Webhook.Kit.slnx" -c Release
dotnet test "Ehsan.Webhook.Kit.slnx" -c Release
dotnet format "Ehsan.Webhook.Kit.slnx" --verify-no-changes --no-restore
```

There is no separate repository lint script; the Release build runs the configured .NET analyzers with warnings treated as errors. The Redis real-service tests are opt-in and are skipped when `WEBHOOKKIT_REDIS_CONNECTION` is not configured; the committed standalone local gate separately records their successful execution.

Build each sample explicitly:

```powershell
dotnet build "samples\MinimalApi\MinimalApi.csproj" -c Release
dotnet build "samples\Mvc\Mvc.csproj" -c Release
dotnet build "samples\Redis\Redis.csproj" -c Release
dotnet build "samples\EntityFrameworkCore\EntityFrameworkCore.csproj" -c Release
```

Each sample README contains the matching environment variable, run command, port, request file, current-signature generation steps, and expected status. See [Docs/verification.md](Docs/verification.md) for the verification matrix and known external gaps.

## Contributing

Keep changes provider-neutral unless the change belongs in an optional provider package. Add or update focused tests for shared contracts, preserve the raw-body and safe-output boundaries, and run the build, test, and format commands above before review. Do not commit credentials, raw live payloads, generated databases, or unsafe diagnostic output. Keep public API examples aligned with the delivered source and sample projects.

## License

WebhookKit is released under the [MIT License](LICENSE.txt).
