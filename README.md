# WebhookKit

> Provider-agnostic webhook infrastructure for ASP.NET Core — HMAC verification, atomic deduplication, retry, and pluggable storage in one kit.

[![NuGet](https://img.shields.io/nuget/v/WebhookKit.AspNetCore.svg?label=WebhookKit.AspNetCore)](https://www.nuget.org/packages/WebhookKit.AspNetCore)
[![NuGet](https://img.shields.io/nuget/v/WebhookKit.Core.svg?label=WebhookKit.Core)](https://www.nuget.org/packages/WebhookKit.Core)
[![CI](https://github.com/0ehsan-sh0/Ehsan.Webhook.Kit/actions/workflows/ci.yml/badge.svg)](https://github.com/0ehsan-sh0/Ehsan.Webhook.Kit/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE.txt)

---

## What WebhookKit does

When you receive webhooks from Stripe, GitHub, or any other provider, you need to:

- Verify the HMAC signature on the **exact raw bytes** before touching the payload
- Check the timestamp is fresh to block replay attacks
- Deduplicate retries so your handler runs **exactly once**
- Store the record and handle retry / recovery for async flows

WebhookKit wires all of that up with a single `AddWebhookKit` call and a typed handler. Your code only sees a clean, deserialized event object.

**Targets:** `net8.0` · `net9.0` · `net10.0`

---

## Packages

| Package | Purpose | Install |
|---|---|---|
| `WebhookKit.AspNetCore` | Minimal API & MVC integration (**start here**) | `dotnet add package WebhookKit.AspNetCore` |
| `WebhookKit.Core` | HMAC verification, in-memory store, retry engine | pulled in automatically |
| `WebhookKit.Abstractions` | Shared contracts and interfaces | pulled in automatically |
| `WebhookKit.Redis` | Redis-backed durable store | `dotnet add package WebhookKit.Redis` |
| `WebhookKit.EntityFrameworkCore` | EF Core store | `dotnet add package WebhookKit.EntityFrameworkCore` |
| `WebhookKit.Testing` | Test helpers — fake clock, signed request builder | `dotnet add package WebhookKit.Testing` |

Most applications only need one install command:

```bash
dotnet add package WebhookKit.AspNetCore
```

---

## Quick start — Minimal API

Five steps from zero to a verified, deduplicated webhook endpoint.

### 1. Install

```bash
dotnet add package WebhookKit.AspNetCore
```

### 2. Register services

```csharp
// Program.cs
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore.DependencyInjection;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Options;

var builder = WebApplication.CreateBuilder(args);

// Read the shared secret from config — never hardcode it
var secret = builder.Configuration["WEBHOOKKIT_PROVIDER_SECRET"]
    ?? throw new InvalidOperationException("Set WEBHOOKKIT_PROVIDER_SECRET before starting.");

builder.Services.AddWebhookKit(options =>
{
    options.AddProvider("payments", provider =>
    {
        provider.Signature.HeaderName  = "X-Webhook-Signature";
        provider.Signature.Algorithm   = WebhookHashAlgorithm.HmacSha256;
        provider.Signature.Encoding    = WebhookSignatureEncoding.Hex;
        provider.Signature.Input       = WebhookSignatureInput.RawBody;
        provider.Signature.Secret      = secret;
        provider.Timestamp.HeaderName  = "X-Webhook-Timestamp";
        provider.Timestamp.Tolerance   = TimeSpan.FromMinutes(5);
        provider.EventIdHeaderName     = "X-Webhook-Event-Id";
        provider.EventTypeHeaderName   = "X-Webhook-Event-Type";
    });
});

builder.Services.AddWebhookKitAspNetCore();
builder.Services.AddWebhookHandler<PaymentCompletedHandler>("payment.completed");
```

### 3. Map the endpoint

```csharp
var app = builder.Build();
app.MapWebhook("/webhooks/payments", "payments");
app.Run();
```

### 4. Write a handler

Your handler receives a strongly typed event model — no raw bytes, no HTTP plumbing:

```csharp
public sealed record PaymentCompleted(string PaymentId, decimal Amount);

public sealed class PaymentCompletedHandler : IWebhookHandler<PaymentCompleted>
{
    public Task HandleAsync(
        PaymentCompleted eventData,
        WebhookContext context,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Payment {eventData.PaymentId} — ${eventData.Amount}");
        return Task.CompletedTask;
    }
}
```

### 5. Send a test request

```http
POST /webhooks/payments HTTP/1.1
Content-Type: application/json
X-Webhook-Event-Id: evt_123
X-Webhook-Event-Type: payment.completed
X-Webhook-Timestamp: <current Unix seconds>
X-Webhook-Signature: <lowercase hex HMAC-SHA256 of the body>

{"paymentId":"pay_123","amount":1250}
```

A valid first request returns `200 OK`. A duplicate returns `200 OK` without running the handler again.

---

## Quick start — MVC

Use the same DI registration and add the `[WebhookEndpoint]` attribute to your controller action:

```csharp
// Program.cs — same AddWebhookKit + AddWebhookKitAspNetCore registration as above
builder.Services.AddControllers();
```

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
        var ev = context.GetPayload<PaymentCompleted>();
        // your logic here
        return Ok();
    }
}
```

---

## Storage options

### In-memory (default)

Zero config. Safe for local development and single-process apps. Records disappear on restart.

```csharp
builder.Services.AddWebhookKit(options => { ... });
// That's it — in-memory store is registered automatically.
```

### Redis

Install the package, then call `AddWebhookKitRedis` **after** `AddWebhookKit`:

```csharp
dotnet add package WebhookKit.Redis
```

```csharp
builder.Services.AddWebhookKitRedis(
    builder.Configuration["WEBHOOKKIT_REDIS_CONNECTION"]!,
    options =>
    {
        options.KeyPrefix              = "payments-webhooks";
        options.DeduplicationRetention = TimeSpan.FromDays(7);
        options.RecordRetention        = TimeSpan.FromDays(30);
    });
```

### Entity Framework Core

Install the package and the EF provider for your database:

```bash
dotnet add package WebhookKit.EntityFrameworkCore
dotnet add package Microsoft.EntityFrameworkCore.Sqlite   # or SqlServer, Npgsql, …
```

```csharp
// In your DbContext
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.ApplyWebhookConfiguration(); // maps the WebhookRecord entity
}

// In Program.cs
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite(builder.Configuration["ConnectionStrings:Webhooks"]));
builder.Services.AddWebhookKitEntityFrameworkCore<AppDbContext>();
```

---

## Asynchronous (queue + worker) mode

The default is synchronous — the handler runs before the `200 OK` is sent. To return `202 Accepted` immediately and process in the background:

```csharp
builder.Services.AddWebhookKit(options =>
{
    options.Background.Enabled           = true;
    options.Background.WorkerConcurrency = 2;
    options.Background.ShutdownDrainTimeout = TimeSpan.FromSeconds(5);
    options.AddProvider("payments", provider => { ... });
});
```

```csharp
using WebhookKit.AspNetCore.Pipeline;
using WebhookKit.Core.Options;

app.MapWebhook("/webhooks/payments", new WebhookEndpointOptions("payments")
{
    Mode = WebhookProcessingMode.Asynchronous
});
```

The worker picks up persisted records, applies retry with exponential backoff, and recovers stale leases automatically.

---

## Configuration reference

### Global options (`WebhookKitOptions`)

| Option | Default | Description |
|---|---|---|
| `MaxRequestBodySizeBytes` | `1 048 576` | Body limit before returning `413`. |
| `Storage.PersistRawBody` | `true` | Persist exact request bytes to the store. |
| `Storage.DiscardRawBodyAfterSuccessfulSync` | `false` | Free stored bytes after a successful synchronous delivery. |
| `Queue.Capacity` | `1024` | Bounded in-process queue size for async mode. |
| `Background.Enabled` | `false` | Start the background worker. |
| `Background.WorkerConcurrency` | `1` | Parallel handler tasks in the worker. |
| `Background.ShutdownDrainTimeout` | `5 s` | Graceful-drain window on shutdown. |

### Provider options (`WebhookProviderOptions`)

| Option | Default | Description |
|---|---|---|
| `Signature.HeaderName` | — | Header carrying the provider signature. Required to enable verification. |
| `Signature.Algorithm` | `HmacSha256` | `HmacSha256` or `HmacSha512`. |
| `Signature.Encoding` | `Hex` | `Hex` (case-insensitive) or `Base64`. |
| `Signature.Input` | `RawBody` | `RawBody` or `TimestampPrefixedRawBody`. |
| `Signature.Secret` | — | Primary shared secret. Never commit to source. |
| `Signature.AdditionalSecrets` | `[]` | Rotation secrets accepted in parallel. |
| `Timestamp.HeaderName` | — | Timestamp header. Required unless `AllowMissing = true`. |
| `Timestamp.Tolerance` | `5 min` | Allowed clock skew in either direction. |
| `EventIdHeaderName` | — | Header for the provider event identifier. |
| `EventTypeHeaderName` | — | Header for the event type. |
| `AllowBodyHashFallback` | `false` | Use a deterministic body hash when no Event ID exists. |
| `MaxRequestBodySizeBytes` | *(global)* | Per-provider override. |
| `Retry.MaxAttempts` | `3` | Total attempts including the first. |
| `Retry.InitialDelay` | `2 s` | Delay before the first retry. |
| `Retry.BackoffMultiplier` | `2` | Exponential multiplier per attempt. |
| `Retry.UseJitter` | `true` | Add random jitter to retry delays. |

---

## Secret rotation

Add up to N previous secrets to `AdditionalSecrets` during a rotation window. WebhookKit checks the primary secret first, then each additional secret, all in constant time:

```csharp
provider.Signature.Secret = builder.Configuration["Payments:Secret"];

var previous = builder.Configuration["Payments:PreviousSecret"];
if (!string.IsNullOrWhiteSpace(previous))
    provider.Signature.AdditionalSecrets.Add(previous);
```

Remove `PreviousSecret` from configuration once all in-flight requests signed with it have been processed.

---

## Response codes

| Outcome | Sync | Async | Problem `code` |
|---|:---:|:---:|---|
| Processed | `200` | `202` | `processed` |
| Duplicate | `200` | `202` | `duplicate` |
| Unknown event type (ignored) | `200` | `202` | `ignored` |
| Missing event ID | `400` | `400` | `event-id-required` |
| Missing event type | `400` | `400` | `missing-event-type` |
| Invalid payload | `400` | `400` | `payload-invalid` |
| Invalid / stale timestamp | `400` | `400` | `timestamp-verification-failed` |
| Invalid / missing signature | `401` | `401` | `signature-verification-failed` |
| Body over limit | `413` | `413` | `payload-too-large` |
| Processing failure | `500` | `500` | `handler-failed` |
| Queue full / unavailable | — | `503` | `queue-unavailable` |

Error responses use `application/problem+json`. They never echo secrets, raw bodies, signatures, or exception messages.

---

## Testing helpers

```bash
dotnet add package WebhookKit.Testing
```

Build deterministic, signed test requests without a running server:

```csharp
using WebhookKit.Testing;

var clock   = new FakeWebhookClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
var builder = WebhookTestRequestBuilder
    .Create("payments", clock)
    .WithEventId("evt_test_1")
    .WithEventType("payment.completed")
    .WithJson(new { paymentId = "pay_1", amount = 100 });

var generator  = new WebhookSignatureGenerator("your-test-secret");
using var request = builder.BuildSigned(generator);
// Pass `request` to HttpClient or WebApplicationFactory
```

`WebhookTestHarness<TApplication>` wraps `WebApplicationFactory<T>` for full integration tests.

---

## Security

- Signature comparison uses `CryptographicOperations.FixedTimeEquals` — no timing leak.
- Timestamp freshness is validated before handler invocation.
- Body size is checked against `Content-Length` **and** a proof byte before memory is used.
- Safe problem responses never contain secrets, raw bodies, parser errors, or stack traces.
- Raw bodies and headers persisted in Redis / EF can include sensitive data — encrypt, restrict, and retain according to your policy.

Read [Docs/security-audit.md](Docs/security-audit.md) before deploying to production.

---

## Troubleshooting

| Symptom | Check |
|---|---|
| `401` on every request | Secret mismatch, wrong encoding (hex vs base64), or body was mutated after signing. Sign the exact bytes you send. |
| `400` timestamp failure | Generate the Unix-seconds timestamp immediately before sending — not at request build time. |
| `400` missing event ID | Configure `EventIdHeaderName` / `EventTypeHeaderName`, or enable `AllowBodyHashFallback` after reading its tradeoffs. |
| `400` payload invalid | Body is malformed JSON, or `JsonSerializerOptions` naming policy doesn't match. |
| `413` payload too large | Raise `MaxRequestBodySizeBytes` on the global or provider options. |
| `503` queue unavailable | Worker not started (`Background.Enabled = false`) or queue is full. |
| Duplicate acknowledged, handler didn't run | Expected — atomic deduplication fired. Inspect the store record. |
| Redis connection failure | Verify `WEBHOOKKIT_REDIS_CONNECTION`, ensure Redis is reachable. |
| EF unique-key violation | `ApplyWebhookConfiguration()` not called in `OnModelCreating`, or schema is out of date. |

---

## Building from source

```powershell
dotnet restore "Ehsan.Webhook.Kit.slnx"
dotnet build  "Ehsan.Webhook.Kit.slnx" -c Release
dotnet test   "Ehsan.Webhook.Kit.slnx" -c Release
dotnet format "Ehsan.Webhook.Kit.slnx" --verify-no-changes --no-restore
```

Redis tests are skipped unless `WEBHOOKKIT_REDIS_CONNECTION` is set.

Runnable samples: [MinimalApi](samples/MinimalApi) · [MVC](samples/Mvc) · [Redis](samples/Redis) · [EF Core](samples/EntityFrameworkCore)

---

## Contributing

- Keep changes provider-neutral unless they belong in an optional provider package.
- Add focused tests for any shared-contract change.
- Preserve the raw-body and safe-output boundaries.
- Run build, test, and format before submitting a PR.
- Never commit credentials, live payloads, generated databases, or unsafe diagnostic output.

---

## License

WebhookKit is released under the [MIT License](LICENSE.txt).
