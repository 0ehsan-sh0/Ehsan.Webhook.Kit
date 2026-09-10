# WebhookKit

[![Build & Test](https://img.shields.io/badge/build-passing-brightgreen.svg)](#)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.txt)
[![Target Frameworks](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-blue.svg)](#)

**Production-Ready Webhook Infrastructure for ASP.NET Core.**

WebhookKit is a modular, provider-agnostic webhook infrastructure library for ASP.NET Core. It solves the critical infrastructure problems encountered when consuming webhooks in production—including cryptographic verification, replay mitigation, atomic deduplication, raw body preservation, background processing, and resilient retries—so you can focus on writing your business logic.

---

## Key Features

- **Provider-Agnostic**: Works with any webhook provider (Stripe, GitHub, Shopify, PayPal, custom webhooks).
- **Cryptographic Verification**: First-class HMAC-SHA256 and HMAC-SHA512 verification with constant-time byte comparisons (`CryptographicOperations.FixedTimeEquals`) to eliminate timing attacks.
- **Replay Protection**: Validates delivery timestamps against a configurable tolerance window using a deterministic clock abstraction (`IWebhookClock`).
- **Atomic Deduplication**: Concurrency-safe event deduplication preventing duplicate event execution during network retries across in-memory, Redis, or relational databases.
- **Raw Request Preservation**: Buffers and preserves raw request byte streams before JSON deserialization to ensure cryptographic integrity.
- **Dual Processing Pipelines**:
  - **Synchronous**: Direct execution within HTTP request lifecycle.
  - **Asynchronous**: Atomic ingestion and persistence with immediate HTTP 202 Accepted, processed via background workers (`ChannelWebhookQueue`).
- **Resilient Retries**: Configurable retry policies with exponential backoff and randomized jitter.
- **Observability**: Structured, zero-allocation logging with automatic sanitization (zero secret leaks) and `System.Diagnostics.ActivitySource` distributed tracing.
- **Modern .NET & Native AOT Friendly**: Multi-targeting `.NET 8`, `.NET 9`, and `.NET 10` with nullable reference types enabled and central package management.

---

## Package Architecture

WebhookKit follows a decoupled multi-package architecture. Consumers only install what they need:

| Package | Purpose | Dependencies |
| :--- | :--- | :--- |
| **`WebhookKit.Abstractions`** | Pure contracts, status enums, and domain representations (`WebhookRecord`, `WebhookContext`). | *None* |
| **`WebhookKit.Core`** | HMAC engine, options, clock, in-memory store, retry engine, queue, and handler registry. | `WebhookKit.Abstractions`, `Microsoft.Extensions.*` |
| **`WebhookKit.AspNetCore`** | Minimal API (`app.MapWebhook`), MVC controller filters/attributes, and raw body reader. | `WebhookKit.Core`, `Microsoft.AspNetCore.App` |
| **`WebhookKit.Redis`** | Distributed deduplication and store provider backed by `StackExchange.Redis`. | `WebhookKit.Abstractions`, `StackExchange.Redis` |
| **`WebhookKit.EntityFrameworkCore`**| Relational store provider and EF Core model builder configurations. | `WebhookKit.Abstractions`, `Microsoft.EntityFrameworkCore` |
| **`WebhookKit.Testing`** | Mock request builders, fake clock (`FakeWebhookClock`), and signature generation utilities. | `WebhookKit.Core` |

---

## Quick Start (Minimal API)

### 1. Register Services

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWebhookKit(options =>
{
    options.AddProvider("payments", provider =>
    {
        provider.Signature.HeaderName = "X-Signature";
        provider.Signature.Algorithm = WebhookHashAlgorithm.HmacSha256;
        provider.Signature.Secret = builder.Configuration["Webhooks:Payments:Secret"];

        provider.Timestamp.HeaderName = "X-Timestamp";
        provider.Timestamp.Tolerance = TimeSpan.FromMinutes(5);

        provider.EventId.HeaderName = "X-Event-Id";
    });
});

// Register strongly typed handler for specific event type
builder.Services.AddWebhookHandler<PaymentCompletedHandler>("payment.completed");

var app = builder.Build();
```

### 2. Map Webhook Endpoint

```csharp
app.MapWebhook("/webhooks/payments", "payments");

app.Run();
```

### 3. Implement Event Handler

```csharp
public sealed class PaymentCompletedHandler : IWebhookHandler<PaymentCompleted>
{
    private readonly ILogger<PaymentCompletedHandler> _logger;

    public PaymentCompletedHandler(ILogger<PaymentCompletedHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(PaymentCompleted payload, WebhookContext context, CancellationToken ct)
    {
        _logger.LogInformation("Processing payment {OrderId} for event {EventId}", 
            payload.OrderId, context.EventId);

        // Execute your business logic here...
    }
}
```

---

## Repository Structure

```
WebhookKit/
├── src/
│   ├── WebhookKit.Abstractions/
│   ├── WebhookKit.Core/
│   ├── WebhookKit.AspNetCore/
│   ├── WebhookKit.Redis/
│   ├── WebhookKit.EntityFrameworkCore/
│   └── WebhookKit.Testing/
├── tests/
│   ├── WebhookKit.Abstractions.Tests/
│   ├── WebhookKit.Core.Tests/
│   ├── WebhookKit.AspNetCore.Tests/
│   ├── WebhookKit.Redis.Tests/
│   ├── WebhookKit.EntityFrameworkCore.Tests/
│   └── WebhookKit.IntegrationTests/
├── samples/
│   ├── MinimalApi/
│   ├── Mvc/
│   ├── Redis/
│   └── EntityFrameworkCore/
├── benchmarks/
│   └── WebhookKit.Benchmarks/
├── Docs/
│   ├── WEBHOOKKIT.md                 # Master Architecture Specification
│   └── adr/                          # Architectural Decision Records
├── CONTEXT.md                        # Ubiquitous Language & Domain Glossary
├── Directory.Build.props             # Central Build & NuGet Configuration
├── Directory.Packages.props          # Central Package Management (CPM)
└── Ehsan.Webhook.Kit.slnx            # Solution File
```

---

## Building & Testing

### Prerequisites
- [.NET 8.0 SDK or later](https://dotnet.microsoft.com/download) (.NET 10 SDK recommended)

### Build Solution
```powershell
dotnet build Ehsan.Webhook.Kit.slnx
```

### Run Tests
```powershell
dotnet test Ehsan.Webhook.Kit.slnx
```

---

## Architecture & Governance

- **Ubiquitous Language**: See [CONTEXT.md](CONTEXT.md) for canonical definitions of domain concepts and entities.
- **Architectural Decision Records**: See [Docs/adr/0001-solution-and-package-architecture.md](Docs/adr/0001-solution-and-package-architecture.md).
- **Master Specification**: See [Docs/WEBHOOKKIT.md](Docs/WEBHOOKKIT.md).

---

## License

This project is licensed under the [MIT License](LICENSE.txt).