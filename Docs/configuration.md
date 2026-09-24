# WebhookKit configuration reference

This reference describes the delivered public configuration surface. The first-run path is in the [root README](../README.md); the four runnable consumers are in [samples](../samples).

## Registration layers

A normal ASP.NET Core application registers the core options and pipeline first, then the ASP.NET adapter and typed handlers:

```csharp
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
builder.Services.AddWebhookHandler<PaymentHandler>("payment.completed");
```

`AddWebhookKit` registers `WebhookKitOptions`, startup validation, `SystemWebhookClock`, the default in-memory store, the default bounded channel, extractors, verifiers, deserialization, handler registry, ingestion service, retry executor, and optional hosted worker. The overload with no configuration action is valid when the application supplies its own services or only needs the defaults.

`AddWebhookKitAspNetCore` registers the body reader, endpoint service, response writer/filter, response formatter, MVC model binder, and the Minimal API/MVC adapters. `AddWebhookHandler<THandler>(string eventType)` registers a concrete scoped handler. The non-generic overload accepts a `Type` plus an event type.

Optional store registrations are additive. Registering a custom `IWebhookStore` after an optional provider call becomes the final resolved store registration.

## `WebhookKitOptions`

| Member | Default | Meaning |
| --- | --- | --- |
| `Providers` | Empty, case-insensitive dictionary | Named provider profiles. `AddProvider` rejects empty and duplicate names. |
| `MaxRequestBodySizeBytes` | `1024 * 1024` | Global body limit. The effective limit is the provider override when present. |
| `Storage` | New `WebhookStorageOptions` | Raw-body persistence and synchronous discard policy. |
| `Queue` | Capacity `1024` | In-process channel capacity. |
| `Background` | Disabled, concurrency `1` | Worker and recovery settings. |
| `JsonSerializerOptions` | `PropertyNameCaseInsensitive = true` | Options passed to `System.Text.Json` for handler payload deserialization. |

The options validator runs through `ValidateOnStart`. It rejects non-positive body limits, invalid queue/background values, unknown enum values, incomplete signature configuration, missing timestamp configuration when it is not explicitly allowed, invalid retry values, and a lease that is not longer than the maximum retry window.

## `WebhookProviderOptions`

| Member | Default | Meaning |
| --- | --- | --- |
| `Signature` | New signature options | HMAC verification settings. |
| `Timestamp` | Header absent, tolerance five minutes, `AllowMissing = false` | Replay-window settings. |
| `Retry` | Three total attempts, two-second initial delay, multiplier `2`, jitter on, ratio `0.2` | Per-provider processing retry settings. |
| `MaxRequestBodySizeBytes` | `null` | Provider-specific body limit override. |
| `AllowBodyHashFallback` | `false` | Explicit fallback for a missing Event ID. |
| `EventIdHeaderName` | `null` | Header-first Event ID extractor. |
| `EventTypeHeaderName` | `null` | Header-first Event Type extractor. |

### Signature options

`WebhookSignatureOptions` contains:

- `HeaderName`: the signature header. Configuring a secret without this header fails validation.
- `Input`: `WebhookSignatureInput.RawBody` by default, or `TimestampPrefixedRawBody`.
- `SigningInput`: an alias for `Input`; prefer `Input` in new code.
- `TimestampSeparator`: UTF-8 separator for timestamp-prefixed input, default `.`.
- `Separator`: an alias for `TimestampSeparator`.
- `Algorithm`: `WebhookHashAlgorithm.HmacSha256` or `HmacSha512`.
- `Encoding`: `WebhookSignatureEncoding.Hex` or `Base64`.
- `Secret`: the current shared secret.
- `AdditionalSecrets`: temporary rotation secrets checked after the current secret.

For `RawBody`, the HMAC input is the exact request body bytes. For `TimestampPrefixedRawBody`, it is UTF-8 timestamp bytes, UTF-8 separator bytes, and the exact body bytes. Timestamp-prefixed input requires a configured timestamp header. The default HMAC verifier accepts common case-insensitive prefixes such as `sha256=`, `sha512=`, `v1=`, and `v0=` before the configured encoding. It compares decoded bytes with `CryptographicOperations.FixedTimeEquals`.

Secret rotation is verification-side: a request signed with any configured current or additional secret is accepted. The secret value must come from configuration, environment variables, or a secret manager and must not be logged or placed in source.

### Timestamp and replay options

`WebhookTimestampOptions` contains `HeaderName`, `Tolerance`, and `AllowMissing`. The verifier accepts Unix seconds, Unix milliseconds, and ISO-8601 values. It compares absolute clock skew with `IWebhookClock.UtcNow` and accepts the exact tolerance boundary.

A provider without a usable timestamp must either configure an alternative replay signal outside this verifier or explicitly set `AllowMissing = true` after documenting the security tradeoff. `AllowMissing` permits an absent or blank header only; a malformed value is still rejected. Timestamp verification is separate from signature verification, and a valid signature does not bypass freshness validation.

## Extraction and identity

The default DI registrations compose these extractors in order:

- `HeaderEventIdExtractor`, then `JsonEventIdExtractor` with the default `id` path.
- `HeaderEventTypeExtractor`, then `JsonEventTypeExtractor` with the default `type` path.

`JsonEventIdExtractor` and `JsonEventTypeExtractor` accept a dotted object path in their constructors. For example:

```csharp
using WebhookKit.Core.Extractors;

var eventId = new JsonEventIdExtractor("data.object.id");
var eventType = new JsonEventTypeExtractor("data.object.type");
```

To replace the default services, register `IWebhookEventIdExtractor` and `IWebhookEventTypeExtractor` before `AddWebhookKit`, or remove the default registrations and add the replacements after it. A composite extractor can preserve header-first behavior while adding a custom JSON path. Custom implementations must honor cancellation, return `null` when no value is available, and avoid copying secrets or raw payloads into logs or exceptions.

Event Type is required after extraction. A present Event Type with no matching handler is acknowledged and stored as `Ignored`. A missing Event Type is a non-retryable payload failure.

The default deduplication key is the normalized provider plus Event ID. If Event ID is absent, the key factory throws unless `AllowBodyHashFallback` is enabled. The explicit fallback hashes the normalized provider UTF-8 bytes, a zero byte, and the exact raw body with SHA-256, producing a provider-scoped `sha256` key. It is deterministic but can collapse semantically different requests with identical bytes; choose it only when the provider has no usable Event ID.

## Body, context, and storage policy

`IWebhookBodyReader.ReadRawBodyAsync` reads the request once, checks `Content-Length`, and bounds streamed reads. The returned `byte[]` is the signature source of truth. `WebhookContext` keeps an internal copy for typed deserialization and does not expose `RawBody` publicly. `WebhookRecord.RawBody` is the persistence representation and can be null according to storage policy.

`WebhookStorageOptions` has two switches:

- `PersistRawBody` defaults to `true`; when false, the record is created with `RawBody = null`.
- `DiscardRawBodyAfterSuccessfulSync` defaults to `false`; when true, a successful synchronous or ignored terminal path updates the record to remove its raw body.

Asynchronous admission requires `PersistRawBody = true`; the validator rejects background processing when it is false. Request headers are captured in a read-only dictionary snapshot and are persisted with records by default. The header and body storage policy is therefore an application data-governance decision, not a logging setting.

## JSON

The default deserializer is `SystemTextJsonWebhookDeserializer`, registered as `IWebhookDeserializer`. It uses the `JsonSerializerOptions` instance on `WebhookKitOptions`. It rejects malformed JSON, JSON `null`, and null generic results with `WebhookPayloadException` without including the body or parser exception text.

Common configuration:

```csharp
using System.Text.Json;

builder.Services.AddWebhookKit(options =>
{
    options.JsonSerializerOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
});
```

Handlers use `context.GetPayload<T>()`; the context caches a successful deserialization per requested type. Custom `IWebhookDeserializer` implementations can replace the default through DI.

## Processing modes and endpoint options

`MapWebhook(string pattern, string providerName)` is the synchronous overload. The advanced overload accepts `WebhookEndpointOptions`:

| Member | Default | Meaning |
| --- | --- | --- |
| `ProviderName` | Required | Named profile selected by the route or attribute. |
| `Mode` | `Synchronous` | `Synchronous` or `Asynchronous`; `Async` and `Background` are aliases. |
| `IncludeInSchema` | `true` | Whether the endpoint is included in OpenAPI metadata. |
| `OperationId` | `null` | Optional route operation name. |
| `Summary` and `Description` | `null` | Optional OpenAPI text. |
| `Tags` | Empty | OpenAPI tags copied when the route is mapped. |
| `Response` | New response options | Per-outcome status customization. |

`WebhookEndpointResponseOptions` exposes `SuccessStatusCode`, `AcceptedStatusCode`, `DuplicateStatusCode`, `IgnoredStatusCode`, `InvalidSignatureStatusCode`, `InvalidTimestampStatusCode`, `MissingEventIdStatusCode`, `MissingEventTypeStatusCode`, `PayloadInvalidStatusCode`, `PayloadTooLargeStatusCode`, `QueueUnavailableStatusCode`, `ProcessingFailedStatusCode`, `ConfigurationErrorStatusCode`, and `StatusCodeSelector`. Status values must be between 100 and 599.

MVC uses `[WebhookEndpoint(providerName)]`. Its `Mode` and `ProcessingMode` properties are aliases, and its default `ActionPolicy` is `WebhookEndpointActionPolicy.Processed`. The other policy values are `Accepted`, `Duplicate`, `Ignored`, `Handled`, and `All`.

## Queue, leases, and recovery

`ChannelWebhookQueue` is the default `IWebhookQueue`. Its `TryEnqueueAsync` method returns `false` when the bounded channel cannot accept a work item. The endpoint maps that result to `503`; the persisted record is not deleted.

The store owns the delivery state machine. `TryClaimAsync` atomically claims `Received` records or expired-lease `Processing` records, increments `AttemptCount`, and installs `ProcessingLeaseOwner` and `ProcessingLeaseExpiresAt`. `ReleaseAsync`, `MarkProcessedAsync`, and `MarkFailedAsync` require the current owner. `GetRecoverableAsync` returns bounded waiting or expired-lease work. The hosted `WebhookBackgroundWorker` owns polling, scope creation, claims, processing, retries, and terminal transitions.

The defaults are:

| Setting | Default |
| --- | --- |
| `Queue.Capacity` | `1024` |
| `Background.WorkerConcurrency` | `1` |
| `Background.RecoveryInterval` | `30 seconds` |
| `Background.RecoveryBatchSize` | `100` |
| `Background.LeaseDuration` | `2 minutes` |
| `Background.RecoveryAge` | `30 seconds` |

The channel is a notification mechanism, not durable storage. Redis or EF persistence makes the record recoverable; it does not make the process-local channel durable.

## Retry behavior

Retry settings live on each provider:

```csharp
using WebhookKit.Abstractions.Exceptions;

builder.Services.AddWebhookKit(options =>
{
    options.AddProvider("payments", provider =>
    {
        provider.Retry.MaxAttempts = 3;
        provider.Retry.InitialDelay = TimeSpan.FromSeconds(2);
        provider.Retry.BackoffMultiplier = 2;
        provider.Retry.UseJitter = true;
        provider.Retry.JitterRatio = 0.2;
    });
});
```

`MaxAttempts` includes the initial attempt. With the defaults, retry delays are based on two seconds and four seconds before exponential growth, with the configured positive jitter ratio applied to each base delay. Only `WebhookRetryableException` is retryable. `WebhookPermanentException`, `WebhookPayloadException`, cancellation, and unknown exceptions are terminal. `WebhookRetryClassifier` is the public classification seam; `WebhookRetryExecutor` composes the configured policy around typed handler dispatch.

## Optional stores

### Redis

`AddWebhookKitRedis` has overloads for `IConnectionMultiplexer` and a connection string, with optional `RedisWebhookStoreOptions` configuration. Defaults are:

- `KeyPrefix = "webhookkit"`.
- `DeduplicationRetention = TimeSpan.FromDays(7)`.
- `RecordRetention = TimeSpan.FromDays(30)`.

The two TTLs are independent. Redis scripts atomically create the dedup marker, record, and recovery index, and state transitions use `KEEPTTL`. The key prefix validation and key hashing are part of the package boundary. Real Redis behavior remains an external validation item in this workspace.

### Entity Framework Core

`AddWebhookKitEntityFrameworkCore<TContext>()` registers a scoped `EfCoreWebhookStore<TContext>`. `ApplyWebhookConfiguration()` maps the `WebhookEntity`, unique `DeduplicationKey`, status, raw body, header JSON, lease, recovery indexes, and concurrency version. The application owns its `DbContext`, provider, schema creation, migrations, and deployment. The package never calls `EnsureCreated` or `Migrate`.

## Testing configuration

`WebhookKit.Testing` targets the same three library frameworks and brings `Microsoft.AspNetCore.Mvc.Testing` for its generic harness. Use `FakeWebhookClock` for deterministic time, `WebhookTestRequestBuilder` for exact request construction, `WebhookSignatureGenerator` for signing, and `WebhookTestHarness` or `WebhookTestHarness<TApplication>` for host lifecycle tests. The application still owns test configuration and secret injection.

## Related documents

- [Root user guide](../README.md)
- [Verification notes](verification.md)
- [Security audit](security-audit.md)
- [Task 29 sample evidence](../.superpowers/sdd/2026-09-24-webhook-kit-completion/task-29-report.md)
