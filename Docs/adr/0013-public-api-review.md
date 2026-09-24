# 0013: Public API Review

## Status

Accepted for the `1.0.0` source surface on 2026-09-24. This review covers the six shippable assemblies:

- `WebhookKit.Abstractions`
- `WebhookKit.Core`
- `WebhookKit.AspNetCore`
- `WebhookKit.Redis`
- `WebhookKit.EntityFrameworkCore`
- `WebhookKit.Testing`

The review distinguishes consumer contracts from replaceable infrastructure implementations. The implementation is documented by `Docs/configuration.md` and `README.md`; this ADR records the API decisions and their compatibility consequences.

## Context

The first source inventory contained 106 exported types. It also exposed mutable header values, nullable correlation metadata after normal ingestion, two processor interfaces, public default store/verifier/queue/worker implementations, and package references that were not needed by the shipped source.

The review required decisions for naming, nullability, cancellation, result shapes, default behavior, exception behavior, thread safety, XML documentation, package boundaries, and the documented simple and advanced consumer paths.

## Decisions

### 1. Preserve one meaning for delivery identity

`WebhookId` is the WebhookKit-generated identifier for one HTTP transmission. `EventId` is the provider's business-event identifier. `CorrelationId` is an application/diagnostic correlation value. These names are not compatibility aliases for one another.

`WebhookRecord.Id` is the persisted record's transmission identifier and corresponds to the `WebhookId` value on the request and context by domain meaning; it is not an additional event or correlation identity. No new identity aliases were added.

### 2. Make header ownership explicit

The public header contract is:

```csharp
IReadOnlyDictionary<string, IReadOnlyList<string>>
```

This applies to `WebhookContext.Headers`, `WebhookRecord.Headers`, `WebhookVerificationContext.Headers`, `WebhookIngestionRequest.Headers`, and `WebhookTestRequestBuilder.Headers`. Each public contract snapshots the supplied dictionary and each value list. The snapshot is case-insensitive, and callers cannot mutate a returned value through `IList<T>`.

This is a source-level breaking change for consumers that assigned or consumed `string[]` values. It prevents request-header metadata from being changed after verification or persistence has captured it.

### 3. Separate input nullability from resolved correlation metadata

`WebhookIngestionRequest.CorrelationId` remains nullable because it is an input DTO. The ingestion service resolves the value from the explicit request value, the current ambient activity, or a generated fallback.

`WebhookRecord.CorrelationId` and `WebhookContext.CorrelationId` are non-null after resolution. A generated fallback is published atomically for concurrent readers, remains stable for the lifetime of the object, and is distinct from its Webhook ID and Event ID. Provider-specific storage mappings normalize legacy nullable storage values into this resolved contract.

### 4. Use one result-oriented processor contract

`IWebhookProcessor` and its `ProcessAsync` method are removed. `IWebhookDispatchProcessor.DispatchAsync` returns `Task<WebhookDispatchResult>` and accepts a `CancellationToken`. The result distinguishes processed, ignored, and failed dispatch outcomes and carries safe failure metadata.

The old interface is not retained as a second implementation target. The default processor is internal; consumers and tests use the result-oriented interface.

### 5. Hide replaceable infrastructure implementations

The following implementation types are internal because consumers can replace them through their public interfaces:

- the in-memory store, channel queue, hosted worker, retry executor, deduplicator, key factory, and ID/clock defaults;
- HMAC and timestamp verifier implementations;
- the default JSON deserializer, handler registry implementation, processor, and options validator;
- the ASP.NET body reader and default response formatter;
- the Redis store;
- the EF Core store and provider-neutral unique-constraint detector.

Public extension seams such as `IWebhookStore`, `IWebhookQueue`, `IWebhookSignatureVerifier`, `IWebhookTimestampVerifier`, `IWebhookDeserializer`, `IWebhookRetryDelay`, `IWebhookRetryClassifier`, `IWebhookEndpointService`, `IWebhookBodyReader`, `IWebhookResponseFormatter`, `WebhookEntity`, and `ApplyWebhookConfiguration` remain public where consumers need to customize or own those concerns.

### 6. Keep raw-body and header ownership separate

The body reader returns the exact bytes used for signature verification. `WebhookContext` keeps a private copy for typed deserialization and does not expose raw body bytes. `WebhookRecord.RawBody` remains a mutable operational `byte[]?` because stores use it for persistence and terminal raw-body removal. Store implementations copy raw-body buffers on input and output; callers that retain a record should treat its returned buffer as owned data.

This decision preserves the existing persistence contract while keeping raw body out of business handler contexts. Headers have stronger copy-on-capture semantics because they are exposed to handlers and persisted as metadata.

### 7. Keep explicit compatibility aliases where they are not contradictory

Existing aliases such as `SigningInput`, `Separator`, `Async`, `Background`, `ProcessingMode`, response-status aliases, `Admitted`, `IngestForAsyncAsync`, `AddWebhookAspNetCore`, and testing-builder signature/body aliases remain as documented compatibility conveniences. They do not alias Webhook ID, Event ID, or Correlation ID and do not create a second processing or storage contract.

New code should use the primary names: `Input`, `TimestampSeparator`, `Asynchronous`, `Mode`, `Response`, `Accepted`, `AdmitAsync`, `AddWebhookKitAspNetCore`, `WithJson`, and `WithRawBody`.

### 8. Require cancellation and safe failures

All asynchronous public operations accept `CancellationToken` with a default value. Expected verification and processing failures are represented by result/status contracts with safe codes and reasons. Cancellation is propagated rather than converted into a handler failure. Default stores and workers retain their existing atomic, thread-safe state-transition behavior.

## Package boundaries

The review removed unused package references from the shipped projects:

| Package | Direct package references retained |
| --- | --- |
| `WebhookKit.Abstractions` | None |
| `WebhookKit.Core` | `Microsoft.Extensions.Options`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions` |
| `WebhookKit.AspNetCore` | `Microsoft.AspNetCore.App` framework reference; Core project reference |
| `WebhookKit.Redis` | `StackExchange.Redis`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Options` |
| `WebhookKit.EntityFrameworkCore` | `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Relational`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Options` |
| `WebhookKit.Testing` | `Microsoft.AspNetCore.Mvc.Testing`; Core project reference |

Abstractions remain free of ASP.NET Core, Redis, and EF Core dependencies. Core remains free of ASP.NET Core, Redis, and EF Core dependencies. Redis and EF Core depend only on Abstractions plus their provider-specific dependencies. The integration API test directly references all six source projects so the consumer surface is compile-checked rather than inferred only through transitive references.

## API inventory

The reflection inventory used the built `net8.0` assemblies and counted exported types.

| Assembly | Before | After | Change |
| --- | ---: | ---: | ---: |
| `WebhookKit.Abstractions` | 24 | 23 | Removed `IWebhookProcessor` |
| `WebhookKit.Core` | 49 | 33 | Internalized default implementations |
| `WebhookKit.AspNetCore` | 18 | 16 | Internalized body reader and default formatter |
| `WebhookKit.Redis` | 3 | 2 | Internalized Redis store |
| `WebhookKit.EntityFrameworkCore` | 6 | 4 | Internalized EF store and provider detector |
| `WebhookKit.Testing` | 6 | 6 | No exported type removed |
| **Total** | **106** | **84** | **22 implementation/obsolete types removed** |

The focused API tests in `tests/WebhookKit.IntegrationTests/PublicApiTests.cs` compile the documented Minimal API paths, handler/store/provider registrations, testing request builder, dispatch/store shapes, header ownership, correlation nullability, and internal implementation boundary.

## Consequences

- Consumers receive a smaller, intentional surface and must depend on interfaces rather than default implementation classes.
- The header contract and resolved correlation behavior are stronger than the initial shape; the header change is intentionally breaking before `1.0.0` publication.
- Existing non-identity compatibility aliases continue to compile while primary names are documented for new code.
- Raw-body mutability remains limited to the persistence record contract; business contexts remain shielded from raw bytes.
- Package consumers receive fewer direct package references, and XML documentation is required for all exported source members.
- External publication, release tagging, and live Redis validation remain outside this review.
