# WebhookKit Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `subagent-driven-development` (recommended) or `executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the remaining WebhookKit roadmap from storage and endpoint integration through optional persistence, testing, samples, documentation, package validation, CI, benchmarks, and a locally validated v1.0.0 release candidate.

**Architecture:** Keep `WebhookKit.Abstractions` provider-neutral and dependency-free. Compose verification, deduplication, context creation, handler dispatch, retry, and diagnostics in `WebhookKit.Core`; expose one ingestion pipeline through Minimal API and MVC adapters in `WebhookKit.AspNetCore`. Implement Redis and EF Core as optional store providers without coupling the core package to either technology. Use a bounded in-process channel for notification, the store for claims and recovery, and explicit release gates for packages and external publication.

**Tech Stack:** C# latest, .NET 8/9/10, ASP.NET Core, `System.Text.Json`, `System.Threading.Channels`, `BackgroundService`, xUnit, FluentAssertions, NSubstitute, StackExchange.Redis, Entity Framework Core, SQLite, BenchmarkDotNet, GitHub Actions, PowerShell package inspection.

**Spec:** `Docs/WEBHOOKKIT.md`; domain decisions in `CONTEXT.md`; accepted architecture decisions in `Docs/adr/0001-solution-and-package-architecture.md` through `Docs/adr/0012-release-readiness-and-user-documentation.md`.

## Global Constraints

- Keep the existing `net8.0;net9.0;net10.0` library target set; test projects and samples target `net8.0` unless a test matrix requires another target.
- Keep `WebhookKit.Abstractions` free of ASP.NET Core, Redis, EF Core, and third-party package dependencies.
- Treat raw request bytes as pipeline data, not handler data; handlers receive `WebhookContext.GetPayload<T>()`, metadata, and headers.
- Keep synchronous processing as the default; asynchronous processing is explicit and its in-process channel is bounded and non-durable.
- Require provider Event IDs for deduplication by default; permit only an explicitly configured deterministic body-hash fallback.
- Never log or return secrets, signatures, raw bodies, parser details, or exception messages.
- Keep handler execution sequential in registration order and fail fast.
- Use atomic store claims and time-bounded processing leases; completed deliveries must not be reclaimed.
- Use three total retry attempts by default, two-second initial delay, multiplier `2`, and jitter enabled.
- Redis dedup retention defaults to seven days and full-record retention to thirty days.
- The EF Core package provides configuration and store integration; the consuming application owns its `DbContext` and migrations.
- Do not publish packages, create release tags, or alter git configuration without a separate explicit user request.
- Run the existing first-two-task test coverage and add a regression test whenever a shared contract changes.
- Do not add code comments unless explicitly requested.

---

## File Map

### Contracts and shared domain

- Modify `src/WebhookKit.Abstractions/WebhookRecord.cs` for deduplication key and processing lease fields.
- Modify `src/WebhookKit.Abstractions/WebhookContext.cs` for hidden raw-body ownership, typed payload access, and payload caching.
- Modify `src/WebhookKit.Abstractions/IWebhookStore.cs` for claims, leases, terminal transitions, and recovery queries.
- Create `src/WebhookKit.Abstractions/IWebhookDeserializer.cs` for the provider-neutral payload deserializer seam.
- Create `src/WebhookKit.Abstractions/IWebhookQueue.cs` and `src/WebhookKit.Abstractions/WebhookWorkItem.cs`.
- Create `src/WebhookKit.Abstractions/IWebhookResponseFormatter.cs`.
- Create `src/WebhookKit.Abstractions/Exceptions/WebhookRetryableException.cs`, `WebhookPermanentException.cs`, and `WebhookPayloadException.cs`.

### Core engine

- Create `src/WebhookKit.Core/Stores/InMemoryWebhookStore.cs`.
- Create `src/WebhookKit.Core/Deduplication/WebhookDeduplicationKeyFactory.cs` and `DefaultWebhookDeduplicator.cs`.
- Create `src/WebhookKit.Core/Deserialization/SystemTextJsonWebhookDeserializer.cs`.
- Create `src/WebhookKit.Core/Handlers/WebhookHandlerRegistry.cs` and `WebhookHandlerDescriptor.cs`.
- Create `src/WebhookKit.Core/Processing/WebhookProcessor.cs` and `WebhookIngestionService.cs`.
- Create `src/WebhookKit.Core/Retries/WebhookRetryPolicy.cs` and `WebhookRetryClassifier.cs`.
- Create `src/WebhookKit.Core/Queues/ChannelWebhookQueue.cs`.
- Create `src/WebhookKit.Core/Workers/WebhookBackgroundWorker.cs`.
- Create `src/WebhookKit.Core/Diagnostics/WebhookDiagnostics.cs` and `WebhookLogMessages.cs`.
- Modify `src/WebhookKit.Core/Options/WebhookKitOptions.cs`, `WebhookProviderOptions.cs`, `WebhookSignatureOptions.cs`, `WebhookTimestampOptions.cs`, and `WebhookRetryOptions.cs`.
- Modify `src/WebhookKit.Core/DependencyInjection/WebhookKitServiceCollectionExtensions.cs` for the complete core service graph.

### ASP.NET Core integration

- Create `src/WebhookKit.AspNetCore/Pipeline/IWebhookEndpointService.cs`, `WebhookEndpointService.cs`, and `WebhookEndpointOptions.cs`.
- Create `src/WebhookKit.AspNetCore/Responses/WebhookResponseWriter.cs` and `WebhookProblemResponse.cs`.
- Create `src/WebhookKit.AspNetCore/DependencyInjection/WebhookEndpointRouteBuilderExtensions.cs`.
- Create `src/WebhookKit.AspNetCore/Mvc/WebhookEndpointAttribute.cs` and `WebhookEndpointFilter.cs`.
- Modify `src/WebhookKit.AspNetCore/DependencyInjection/WebhookKitAspNetCoreServiceCollectionExtensions.cs`.

### Optional providers and testing

- Create `src/WebhookKit.Redis/RedisWebhookStore.cs`, `RedisWebhookStoreOptions.cs`, and `RedisServiceCollectionExtensions.cs`.
- Create `src/WebhookKit.EntityFrameworkCore/WebhookEntity.cs`, `WebhookModelBuilderExtensions.cs`, `EfCoreWebhookStore.cs`, and `EfCoreServiceCollectionExtensions.cs`.
- Create `src/WebhookKit.Testing/WebhookTestRequestBuilder.cs`, `WebhookSignatureGenerator.cs`, and `WebhookTestHarness.cs`.

### Tests, samples, and release files

- Add focused tests under the existing `tests/WebhookKit.Abstractions.Tests`, `tests/WebhookKit.Core.Tests`, `tests/WebhookKit.AspNetCore.Tests`, `tests/WebhookKit.Redis.Tests`, and `tests/WebhookKit.EntityFrameworkCore.Tests` projects.
- Add end-to-end tests under `tests/WebhookKit.IntegrationTests`.
- Implement `samples/MinimalApi/Program.cs`, `samples/Mvc/Program.cs`, `samples/Redis/Program.cs`, and `samples/EntityFrameworkCore/Program.cs`, with one README and `.http` request file per sample.
- Rewrite `README.md` as the primary user guide.
- Modify `Directory.Build.props` and `Directory.Packages.props` for package metadata, XML docs, SourceLink, and symbols.
- Create `scripts/validate-packages.ps1`.
- Create `.github/workflows/ci.yml` and `.github/workflows/release.yml`.
- Replace placeholder benchmark code with `benchmarks/WebhookKit.Benchmarks/WebhookKitBenchmarks.cs` and `benchmarks/README.md`.
- Update `Docs/tasks/index.html` and the task detail pages as each workstream is completed.

---

### Task 10: Extend Store Contracts and Implement the In-Memory Store

**Files:**
- Modify: `src/WebhookKit.Abstractions/WebhookRecord.cs`
- Modify: `src/WebhookKit.Abstractions/IWebhookStore.cs`
- Create: `src/WebhookKit.Core/Stores/InMemoryWebhookStore.cs`
- Test: `tests/WebhookKit.Core.Tests/InMemoryWebhookStoreTests.cs`

**Interfaces:**
- Preserve `GetAsync`, `TryCreateAsync`, and `UpdateAsync`.
- Add `GetByWebhookIdAsync(string webhookId, CancellationToken)`.
- Add `TryClaimAsync(string webhookId, string leaseOwner, TimeSpan leaseDuration, CancellationToken)`.
- Add `ReleaseAsync(string webhookId, string leaseOwner, CancellationToken)`.
- Add `MarkProcessedAsync(string webhookId, string leaseOwner, DateTimeOffset processedAt, CancellationToken)`.
- Add `MarkFailedAsync(string webhookId, string leaseOwner, DateTimeOffset failedAt, string? failureReason, CancellationToken)`.
- Add `GetRecoverableAsync(DateTimeOffset now, TimeSpan expiredLeaseAge, int limit, CancellationToken)`.
- Add `DeduplicationKey`, `ProcessingLeaseOwner`, and `ProcessingLeaseExpiresAt` to `WebhookRecord`.

- [ ] **Step 1: Write the failing atomicity tests**

Test that 100 concurrent `TryCreateAsync` calls for the same provider/Event ID produce exactly one `true`, all other calls return `false`, and only one record is queryable. Add tests for lookup by Webhook ID, status update, successful claim, claim rejection while leased, release, processed transition, failed transition, and recovery of `Received` or lease-expired `Processing` records.

- [ ] **Step 2: Run the focused test project and confirm the new tests fail**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~InMemoryWebhookStore"
```

Expected: failure because the expanded store contract and implementation do not exist.

- [ ] **Step 3: Implement the in-memory store**

Use a `ConcurrentDictionary<string, WebhookRecord>` indexed by `DeduplicationKey` and a second `ConcurrentDictionary<string, WebhookRecord>` indexed by `Id`. Protect each claim/update operation with a per-record lock so a stale lease cannot overwrite a newer owner. Copy returned records to keep callers from mutating store state without an explicit update. Validate provider, Event ID, deduplication key, Webhook ID, lease duration, and non-empty lease owner at the contract boundary.

- [ ] **Step 4: Run the focused tests and the existing core tests**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj"
```

Expected: all tests pass with zero warnings.

---

### Task 11: Implement Deduplication Keying and the Deduplication Pipeline

**Files:**
- Create: `src/WebhookKit.Core/Deduplication/WebhookDeduplicationKeyFactory.cs`
- Create: `src/WebhookKit.Core/Deduplication/DefaultWebhookDeduplicator.cs`
- Modify: `src/WebhookKit.Core/Options/WebhookProviderOptions.cs`
- Modify: `src/WebhookKit.Core/DependencyInjection/WebhookKitServiceCollectionExtensions.cs`
- Test: `tests/WebhookKit.Core.Tests/DeduplicationTests.cs`

**Interfaces:**
- `WebhookDeduplicationKeyFactory.Create(string provider, string? eventId, ReadOnlyMemory<byte> rawBody, WebhookProviderOptions options)` returns `provider + ":" + eventId` when Event ID exists.
- When Event ID is absent and `AllowBodyHashFallback` is false, throw a safe `WebhookConfigurationException`.
- When the fallback is enabled, return `provider + ":sha256:" + lowercase hex SHA-256(provider bytes, separator, raw body)`.
- `DefaultWebhookDeduplicator.TryAcquireAsync(WebhookRecord, CancellationToken)` delegates to the store’s atomic create and never performs exists-then-create.

- [ ] **Step 1: Write tests for default and fallback keys**

Assert provider case normalization, stable key output, deterministic fallback output, different bodies producing different keys, missing-EventId rejection by default, and a duplicate acquiring rights exactly once.

- [ ] **Step 2: Run the focused tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Deduplication"
```

Expected: failure because the key factory and default deduplicator are absent.

- [ ] **Step 3: Implement keying and registration**

Use UTF-8 provider bytes, a fixed separator byte, and the exact raw body bytes. Register the key factory and deduplicator as singleton services. Reject duplicate provider keys during configuration and never log a generated key that could reveal payload content beyond its fixed hash form.

- [ ] **Step 4: Run deduplication and store tests**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Deduplication|FullyQualifiedName~InMemoryWebhookStore"
```

Expected: all tests pass and exactly one concurrent claimant succeeds.

---

### Task 12: Add Hidden Raw-Body Ownership and Typed Payload Access

**Files:**
- Modify: `src/WebhookKit.Abstractions/WebhookContext.cs`
- Create: `src/WebhookKit.Abstractions/IWebhookDeserializer.cs`
- Test: `tests/WebhookKit.Abstractions.Tests/WebhookContextTests.cs`
- Test: `tests/WebhookKit.Core.Tests/ContextPayloadTests.cs`

**Interfaces:**
- Add `WebhookContext.GetPayload<T>()` without exposing a public `RawBody` property.
- Add `IWebhookDeserializer.Deserialize<T>(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default)`.
- Cache one successful deserialization per payload type inside the context.
- Reject a null deserializer, null type, or failed deserialization with a safe `WebhookPayloadException`.

- [ ] **Step 1: Write context tests**

Assert metadata and headers are copied safely, raw body is unavailable through the public surface, repeated `GetPayload<T>()` calls return the cached instance, a different generic type is deserialized separately, and invalid JSON raises only a safe payload exception.

- [ ] **Step 2: Run the focused tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.Abstractions.Tests\WebhookKit.Abstractions.Tests.csproj" --filter "FullyQualifiedName~WebhookContext"
```

Expected: failure because the context has no deserializer seam or payload cache.

- [ ] **Step 3: Implement the context boundary**

Store the raw body in an internal field, copy headers into a read-only case-insensitive view, keep a type-keyed concurrent cache, and expose only metadata, headers, and `GetPayload<T>()`. Ensure `ToString()` and debugger-visible representation omit the raw body and all header values.

- [ ] **Step 4: Run the abstraction and core context tests**

Run:

```powershell
dotnet test "tests\WebhookKit.Abstractions.Tests\WebhookKit.Abstractions.Tests.csproj"; dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Context"
```

Expected: all tests pass.

---

### Task 13: Implement System.Text.Json Deserialization

**Files:**
- Create: `src/WebhookKit.Core/Deserialization/SystemTextJsonWebhookDeserializer.cs`
- Modify: `src/WebhookKit.Core/Options/WebhookKitOptions.cs`
- Modify: `src/WebhookKit.Core/DependencyInjection/WebhookKitServiceCollectionExtensions.cs`
- Test: `tests/WebhookKit.Core.Tests/WebhookDeserializerTests.cs`

**Interfaces:**
- Register `IWebhookDeserializer` as a singleton.
- Support configured `JsonSerializerOptions`, including property naming policy and case-insensitive matching.
- Reject malformed JSON, empty JSON, and null results with `WebhookPayloadException`.
- Never include raw JSON, exception messages, or stack traces in the exception message.

- [ ] **Step 1: Write valid, malformed, empty, and cancellation tests**

Use a small payload record, a custom camel-case configuration, an invalid UTF-8/JSON byte array, a JSON `null` document, and a cancelled token where supported.

- [ ] **Step 2: Run the deserializer tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Deserializer"
```

Expected: failure because the deserializer is absent.

- [ ] **Step 3: Implement the deserializer**

Use `JsonSerializer.Deserialize<T>(rawBody.Span, options)`, reject null results, map cancellation to `OperationCanceledException`, and translate `JsonException` to the safe payload exception. Do not log the input.

- [ ] **Step 4: Run the core suite**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj"
```

Expected: all tests pass.

---

### Task 14: Implement the Handler Registry and Processor

**Files:**
- Create: `src/WebhookKit.Core/Handlers/WebhookHandlerDescriptor.cs`
- Create: `src/WebhookKit.Core/Handlers/WebhookHandlerRegistry.cs`
- Create: `src/WebhookKit.Core/Processing/WebhookProcessor.cs`
- Create: `src/WebhookKit.Core/Processing/WebhookIngestionService.cs`
- Modify: `src/WebhookKit.Core/DependencyInjection/WebhookKitServiceCollectionExtensions.cs`
- Test: `tests/WebhookKit.Core.Tests/HandlerRegistryTests.cs`
- Test: `tests/WebhookKit.Core.Tests/WebhookProcessorTests.cs`

**Interfaces:**
- Add `AddWebhookHandler<THandler>(string eventType)` and `AddWebhookHandler(Type implementationType, string eventType)` overloads.
- Register handlers in registration order.
- Resolve each matching handler from the current scope.
- Execute handlers sequentially and stop on the first exception.
- Return `Ignored` for a present but unregistered event type.
- Keep verification, deduplication, extraction, and payload deserialization in `WebhookIngestionService`; `WebhookProcessor` owns only typed handler dispatch.

- [ ] **Step 1: Write registry and dispatch tests**

Test one handler, multiple handlers in order, scoped dependency isolation, unknown event type, null event type, cancellation, first-handler failure, second-handler not invoked after first failure, and a handler that deserializes its payload.

- [ ] **Step 2: Run the focused tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Handler|FullyQualifiedName~Processor"
```

Expected: failure because registry and processor are absent.

- [ ] **Step 3: Implement registration and dispatch**

Store descriptors in a case-sensitive event-type dictionary, validate non-empty event types, use `IEnumerable<Type>` from DI, and create a scope per processor call. Return an explicit dispatch result containing `Processed`, `Ignored`, or `Failed` so the pipeline can choose the response without inspecting exceptions.

- [ ] **Step 4: Run the full core suite**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj"
```

Expected: all tests pass.

---

### Task 15: Implement the Minimal API Endpoint and Shared Ingestion Pipeline

**Files:**
- Create: `src/WebhookKit.AspNetCore/Pipeline/WebhookEndpointOptions.cs`
- Create: `src/WebhookKit.AspNetCore/Pipeline/WebhookEndpointService.cs`
- Create: `src/WebhookKit.AspNetCore/DependencyInjection/WebhookEndpointRouteBuilderExtensions.cs`
- Modify: `src/WebhookKit.AspNetCore/DependencyInjection/WebhookKitAspNetCoreServiceCollectionExtensions.cs`
- Test: `tests/WebhookKit.AspNetCore.Tests/WebhookEndpointServiceTests.cs`

**Interfaces:**
- Add `app.MapWebhook(string pattern, string providerName)`.
- Add an advanced options overload with `WebhookEndpointOptions` for mode, OpenAPI metadata, and response customization.
- Add `IWebhookEndpointService.ProcessAsync(HttpContext, WebhookEndpointOptions, CancellationToken)`.
- Use `IWebhookBodyReader`, effective provider body limit, configured signature/timestamp verification, extraction, deduplication, context creation, and mode-specific processing.

- [ ] **Step 1: Write endpoint service tests**

Test a valid request, invalid signature, expired timestamp, missing Event ID, missing EventType, unknown EventType, duplicate request, oversized body, cancellation, sync success, async accepted, and safe response mapping.

- [ ] **Step 2: Run the focused ASP.NET tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj" --filter "FullyQualifiedName~Endpoint"
```

Expected: failure because the endpoint service and route extensions are absent.

- [ ] **Step 3: Implement the endpoint service and route extension**

Read the body once, calculate the provider-specific limit, generate a Webhook ID with the existing clock-backed ID strategy, build the record, verify signature before parsing payload, verify replay window, extract Event ID/type, create the deduplication key, atomically create the record, and dispatch or enqueue it. Do not deserialize before successful signature and timestamp verification.

- [ ] **Step 4: Run the ASP.NET test project**

Run:

```powershell
dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj"
```

Expected: all tests pass.

---

### Task 16: Implement MVC Endpoint Support

**Files:**
- Create: `src/WebhookKit.AspNetCore/Mvc/WebhookEndpointAttribute.cs`
- Create: `src/WebhookKit.AspNetCore/Mvc/WebhookEndpointFilter.cs`
- Modify: `src/WebhookKit.AspNetCore/DependencyInjection/WebhookKitAspNetCoreServiceCollectionExtensions.cs`
- Test: `tests/WebhookKit.AspNetCore.Tests/WebhookMvcTests.cs`

**Interfaces:**
- Add `[WebhookEndpoint("provider")]` for controller actions.
- The filter calls the same `IWebhookEndpointService` as Minimal API.
- Bind the resulting `WebhookContext` to the action parameter without exposing raw bytes.
- Return the endpoint service’s response and short-circuit controller execution for verification, duplicate, ignored, or failure outcomes unless the action explicitly opts into a handled context.

- [ ] **Step 1: Write MVC filter and binding tests**

Test valid action execution, invalid signature short-circuit, duplicate short-circuit, context injection, and the same response codes as Minimal API.

- [ ] **Step 2: Run the focused tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj" --filter "FullyQualifiedName~Mvc"
```

Expected: failure because the attribute and filter are absent.

- [ ] **Step 3: Implement the attribute and filter**

Register the filter through the application services, select the provider from the attribute, invoke the shared endpoint service, and add only a safe `WebhookContext` action parameter binding path.

- [ ] **Step 4: Run the ASP.NET suite**

Run:

```powershell
dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj"
```

Expected: all tests pass.

---

### Task 17: Implement the Configurable HTTP Response Model

**Files:**
- Create: `src/WebhookKit.Abstractions/IWebhookResponseFormatter.cs`
- Create: `src/WebhookKit.AspNetCore/Responses/WebhookProblemResponse.cs`
- Create: `src/WebhookKit.AspNetCore/Responses/WebhookResponseWriter.cs`
- Modify: `src/WebhookKit.AspNetCore/Pipeline/WebhookEndpointService.cs`
- Test: `tests/WebhookKit.AspNetCore.Tests/WebhookResponseTests.cs`

**Interfaces:**
- Define safe problem fields `Code`, `Message`, and nullable `TraceId`.
- Map sync success/duplicate/ignored to 200, async accepted/duplicate/ignored to 202, invalid signature to 401, replay failure to 400, payload failure to 400, oversized body to 413, queue full to 503, and retry-exhausted processing to 500.
- Allow status-code overrides without allowing body overrides that expose exception details.

- [ ] **Step 1: Write exact status and body tests**

Assert every exit code, safe JSON fields, no raw body, no signature, no secret, no parser detail, and trace ID inclusion only when the host supplies one.

- [ ] **Step 2: Run the response tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj" --filter "FullyQualifiedName~Response"
```

Expected: failure because the formatter and writer are absent.

- [ ] **Step 3: Implement response writing**

Use `Results.Problem` or an equivalent typed ASP.NET response, keep a single safe code per outcome, and ensure errors are written after the endpoint has finished any server-side logging.

- [ ] **Step 4: Run the ASP.NET suite**

Run:

```powershell
dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj"
```

Expected: all tests pass.

---

### Task 18: Add Structured Logging Without Sensitive Values

**Files:**
- Create: `src/WebhookKit.Core/Diagnostics/WebhookLogMessages.cs`
- Modify: `src/WebhookKit.Core/Processing/WebhookIngestionService.cs`
- Modify: `src/WebhookKit.Core/Processing/WebhookProcessor.cs`
- Modify: `src/WebhookKit.Core/Workers/WebhookBackgroundWorker.cs`
- Test: `tests/WebhookKit.Core.Tests/WebhookLoggingTests.cs`

**Interfaces:**
- Log received, verified, rejected, duplicate, ignored, processing, processed, failed, and retry outcomes with Webhook ID, provider, Event ID, Event Type, status, attempt, and trace ID where available.
- Never include raw body, headers, signatures, secrets, or exception messages in structured values.

- [ ] **Step 1: Write logger capture tests**

Use a test `ILogger` provider and assert structured state contains identifiers but does not contain known secret, body, signature, or exception-message substrings.

- [ ] **Step 2: Run the logging tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Logging"
```

Expected: failure because the logging definitions are absent.

- [ ] **Step 3: Implement source-generated log messages**

Use `LoggerMessage` definitions with fixed event IDs and named fields, and classify exceptions only through a safe failure code.

- [ ] **Step 4: Run the core and ASP.NET tests**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj"; dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj"
```

Expected: all tests pass.

---

### Task 19: Add ActivitySource and Correlation Diagnostics

**Files:**
- Create: `src/WebhookKit.Core/Diagnostics/WebhookDiagnostics.cs`
- Modify: `src/WebhookKit.Core/Processing/WebhookIngestionService.cs`
- Modify: `src/WebhookKit.Core/Processing/WebhookProcessor.cs`
- Test: `tests/WebhookKit.Core.Tests/WebhookDiagnosticsTests.cs`

**Interfaces:**
- Expose `WebhookDiagnostics.Source` with name `WebhookKit` and version `1.0.0`.
- Start ingress and handler activities with `webhook.id`, `webhook.provider`, `webhook.event_id`, `webhook.event_type`, and `webhook.status` tags.
- Never tag raw bodies, signatures, secrets, or headers.

- [ ] **Step 1: Write activity tests**

Use an `ActivityListener` to assert the source name, activity names, required tags, and absence of sensitive values.

- [ ] **Step 2: Run the diagnostics tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Diagnostics"
```

Expected: failure because diagnostics are absent.

- [ ] **Step 3: Implement activities and correlation propagation**

Use the current `Activity` or trace identifier when present; otherwise use the Webhook ID as the internal correlation value without confusing it with Event ID.

- [ ] **Step 4: Run diagnostics and core tests**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj"
```

Expected: all tests pass.

---

### Task 20: Implement the Bounded In-Process Queue

**Files:**
- Create: `src/WebhookKit.Abstractions/IWebhookQueue.cs`
- Create: `src/WebhookKit.Abstractions/WebhookWorkItem.cs`
- Create: `src/WebhookKit.Core/Queues/ChannelWebhookQueue.cs`
- Modify: `src/WebhookKit.Core/Options/WebhookKitOptions.cs`
- Modify: `src/WebhookKit.Core/DependencyInjection/WebhookKitServiceCollectionExtensions.cs`
- Test: `tests/WebhookKit.Core.Tests/ChannelWebhookQueueTests.cs`

**Interfaces:**
- Use a bounded `Channel<WebhookWorkItem>` with single-reader/multiple-writer semantics.
- Queue items contain `WebhookId`, `Provider`, and a delivery reference, not a mutable raw-body buffer.
- `TryEnqueueAsync` returns false when the channel is full.
- Queue capacity and full behavior are configurable; the full path maps to 503.

- [ ] **Step 1: Write queue tests**

Test successful enqueue/dequeue, FIFO order within one queue, full capacity, cancellation, completion behavior, and no shared mutable record mutation.

- [ ] **Step 2: Run the queue tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Queue"
```

Expected: failure because the queue contract and implementation are absent.

- [ ] **Step 3: Implement the bounded channel**

Use a configurable capacity, a non-blocking full policy for HTTP admission, and a blocking read with cancellation for the worker. Complete the channel during service shutdown only after the worker has drained or the host cancellation token is signaled.

- [ ] **Step 4: Run the queue tests**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Queue"
```

Expected: all tests pass.

---

### Task 21: Implement the Hosted Worker and Recovery Loop

**Files:**
- Create: `src/WebhookKit.Core/Workers/WebhookBackgroundWorker.cs`
- Modify: `src/WebhookKit.Core/Options/WebhookKitOptions.cs`
- Modify: `src/WebhookKit.Core/DependencyInjection/WebhookKitServiceCollectionExtensions.cs`
- Test: `tests/WebhookKit.Core.Tests/WebhookBackgroundWorkerTests.cs`

**Interfaces:**
- Derive from `BackgroundService`.
- Read a `WebhookWorkItem`, load its record, create a scope, claim the record with a unique lease owner, and invoke the processor.
- Update status to `Processing`, `Processed`, or `Failed`.
- On startup and on a timer, load recoverable records from the store and enqueue their references.
- Default worker concurrency to 1 and make it configurable.

- [ ] **Step 1: Write worker tests**

Test successful dequeue, claim, processing, completion, retryable failure, permanent failure, expired lease recovery, completed record not reclaimed, cancellation, and queue shutdown.

- [ ] **Step 2: Run the worker tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~BackgroundWorker"
```

Expected: failure because the worker is absent.

- [ ] **Step 3: Implement claim-first worker execution**

Generate one lease owner per worker, claim before dispatch, choose a lease longer than the configured retry window, and never dispatch a record that cannot be claimed. Process recoverable records independently of channel notifications.

- [ ] **Step 4: Run worker and queue tests**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~BackgroundWorker|FullyQualifiedName~Queue"
```

Expected: all tests pass.

---

### Task 22: Implement Retry Classification and Exponential Backoff

**Files:**
- Create: `src/WebhookKit.Core/Retries/WebhookRetryClassifier.cs`
- Create: `src/WebhookKit.Core/Retries/WebhookRetryPolicy.cs`
- Modify: `src/WebhookKit.Core/Options/WebhookRetryOptions.cs`
- Modify: `src/WebhookKit.Core/Processing/WebhookProcessor.cs`
- Test: `tests/WebhookKit.Core.Tests/WebhookRetryTests.cs`

**Interfaces:**
- Default to 3 total attempts, 2-second initial delay, multiplier 2, and jitter enabled.
- `WebhookRetryableException` and `WebhookPermanentException` are explicit application controls.
- `OperationCanceledException` and `WebhookPayloadException` are never retried.
- Unknown exceptions are retryable unless the application throws a permanent exception.

- [ ] **Step 1: Write retry tests**

Test attempt count, initial delay, exponential calculation, jitter bounds, retryable exception, permanent exception, cancellation, payload failure, and terminal `Failed` transition.

- [ ] **Step 2: Run retry tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Retry"
```

Expected: failure because the retry engine is absent.

- [ ] **Step 3: Implement the policy**

Calculate `initialDelay * multiplier^(attempt - 1)` for each retry, add bounded nonnegative jitter, cap total attempts, propagate cancellation, and record a safe failure reason after exhaustion.

- [ ] **Step 4: Run the core suite**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj"
```

Expected: all tests pass.

---

### Task 23: Implement the Redis Store and Atomic TTL Claims

**Files:**
- Create: `src/WebhookKit.Redis/RedisWebhookStoreOptions.cs`
- Create: `src/WebhookKit.Redis/RedisWebhookStore.cs`
- Create: `src/WebhookKit.Redis/RedisServiceCollectionExtensions.cs`
- Modify: `src/WebhookKit.Redis/WebhookKit.Redis.csproj`
- Test: `tests/WebhookKit.Redis.Tests/RedisWebhookStoreTests.cs`

**Interfaces:**
- Register `IWebhookStore` with `AddWebhookKitRedis(IConnectionMultiplexer)` or a connection string.
- Use `StringSetAsync(..., When.NotExists)` for atomic dedup creation.
- Store full records under the Webhook ID key with JSON serialization and configurable 30-day retention.
- Use Redis scripts or conditional updates for claim, release, processed, failed, and recoverable transitions.
- Use 7-day default dedup TTL and 30-day default record TTL.

- [ ] **Step 1: Write Redis store tests against a disposable Redis service or test double**

Test atomic creation, duplicate rejection, record round trip, TTL configuration, claim ownership, stale lease reclaim, terminal transitions, and cancellation.

- [ ] **Step 2: Run the focused Redis tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.Redis.Tests\WebhookKit.Redis.Tests.csproj"
```

Expected: failure because the Redis implementation is absent.

- [ ] **Step 3: Implement atomic Redis operations**

Use a stable key prefix and escaped provider/Event ID components. Serialize only store DTOs, never secret options. All state transitions must verify the current lease owner before writing.

- [ ] **Step 4: Run Redis tests and package build**

Run:

```powershell
dotnet test "tests\WebhookKit.Redis.Tests\WebhookKit.Redis.Tests.csproj"; dotnet build "src\WebhookKit.Redis\WebhookKit.Redis.csproj" -c Release
```

Expected: tests pass and the package builds without warnings.

---

### Task 24: Implement EF Core Configuration and Store

**Files:**
- Create: `src/WebhookKit.EntityFrameworkCore/WebhookEntity.cs`
- Create: `src/WebhookKit.EntityFrameworkCore/WebhookModelBuilderExtensions.cs`
- Create: `src/WebhookKit.EntityFrameworkCore/EfCoreWebhookStore.cs`
- Create: `src/WebhookKit.EntityFrameworkCore/EfCoreServiceCollectionExtensions.cs`
- Modify: `src/WebhookKit.EntityFrameworkCore/WebhookKit.EntityFrameworkCore.csproj`
- Test: `tests/WebhookKit.EntityFrameworkCore.Tests/EfCoreWebhookStoreTests.cs`

**Interfaces:**
- Add `ApplyWebhookConfiguration(this ModelBuilder)` with a primary key, provider-safe `DeduplicationKey` unique index, binary raw body mapping, JSON header mapping, status fields, and lease fields.
- Add `EfCoreWebhookStore<TContext>` for an application-owned `DbContext`.
- Catch only provider-specific unique-constraint exceptions and map them to `TryCreateAsync == false`.
- Do not create migrations.

- [ ] **Step 1: Write SQLite In-Memory and EF In-Memory store tests**

Test configuration, atomic duplicate creation, lookup, claim, lease expiration, terminal transitions, raw body round trip, and unique-constraint duplicate mapping.

- [ ] **Step 2: Run the focused EF tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.EntityFrameworkCore.Tests\WebhookKit.EntityFrameworkCore.Tests.csproj"
```

Expected: failure because the entity, configuration, and store are absent.

- [ ] **Step 3: Implement the entity and store**

Map headers to a provider-supported JSON column, raw bytes to a binary/large-object column, and use a normalized provider/Event ID key for uniqueness. Use transactions or provider-specific exception translation only around the atomic create operation.

- [ ] **Step 4: Run EF tests and package build**

Run:

```powershell
dotnet test "tests\WebhookKit.EntityFrameworkCore.Tests\WebhookKit.EntityFrameworkCore.Tests.csproj"; dotnet build "src\WebhookKit.EntityFrameworkCore\WebhookKit.EntityFrameworkCore.csproj" -c Release
```

Expected: tests pass and the package builds without warnings.

---

### Task 25: Complete the Consumer Testing Package

**Files:**
- Create: `src/WebhookKit.Testing/WebhookTestRequestBuilder.cs`
- Create: `src/WebhookKit.Testing/WebhookSignatureGenerator.cs`
- Create: `src/WebhookKit.Testing/WebhookTestHarness.cs`
- Modify: `src/WebhookKit.Testing/WebhookKit.Testing.csproj`
- Test: `tests/WebhookKit.Core.Tests/WebhookTestRequestBuilderTests.cs`

**Interfaces:**
- Build `HttpRequestMessage` requests with provider, event ID, event type, timestamp, arbitrary headers, JSON, or raw bytes.
- Generate HMAC-SHA256 and HMAC-SHA512 signatures in Hex or Base64.
- Support timestamp-prefixed/custom signature inputs and multiple secrets for rotation.
- Provide a harness that starts the consumer’s test host and sends the built request.

- [ ] **Step 1: Write request-builder and signature tests**

Assert exact raw body bytes, header values, timestamp parsing, both algorithms, both encodings, rotation, custom signing input, and deterministic fake clock output.

- [ ] **Step 2: Run the testing-package tests and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~TestRequest"
```

Expected: failure because the request builder and generator are absent.

- [ ] **Step 3: Implement the testing utilities**

Keep the builder immutable after signing, use `HttpRequestMessage` rather than ASP.NET `HttpContext`, and provide no production secret logging.

- [ ] **Step 4: Run the testing-package tests**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~TestRequest"; dotnet build "src\WebhookKit.Testing\WebhookKit.Testing.csproj" -c Release
```

Expected: tests pass and the package builds without warnings.

---

### Task 26: Run the Security Audit

**Files:**
- Create: `tests/WebhookKit.Core.Tests/WebhookSecurityTests.cs`
- Create: `tests/WebhookKit.AspNetCore.Tests/WebhookEndpointSecurityTests.cs`
- Create: `Docs/security-audit.md`

**Checks:**
- Every HMAC comparison uses `CryptographicOperations.FixedTimeEquals`.
- Invalid signature, malformed signature, wrong secret, and encoding mismatch do not reveal which secret or signature failed.
- Oversized bodies are rejected before unbounded memory allocation.
- Raw body, signature, header values, and secrets are absent from logs and external responses.
- Timestamp boundaries reject values outside the configured window and preserve cancellation.
- Deduplication is atomic at 10, 100, and 1,000 concurrent callers.
- Exceptions do not escape the HTTP pipeline as unhandled server errors.

- [ ] **Step 1: Write the security tests before any audit code changes**

Create tests for the checklist above using the existing test doubles and a logger capture provider.

- [ ] **Step 2: Run the security tests and record failures**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Security"; dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj" --filter "FullyQualifiedName~Security"
```

Expected: the new tests either pass or identify the exact missing behavior to fix.

- [ ] **Step 3: Fix every security failure**

Change only the implementation needed to make the tests pass. Keep the external problem response and logging fields safe.

- [ ] **Step 4: Write the audit record**

Document the verified controls and any remaining operational caveat in `Docs/security-audit.md`, then rerun both commands with zero failures.

---

### Task 27: Add Concurrency Stress Tests

**Files:**
- Create: `tests/WebhookKit.Core.Tests/DeduplicationConcurrencyTests.cs`
- Create: `tests/WebhookKit.EntityFrameworkCore.Tests/EfCoreConcurrencyTests.cs`
- Create: `tests/WebhookKit.Redis.Tests/RedisConcurrencyTests.cs`
- Create: `tests/WebhookKit.IntegrationTests/ConcurrentWebhookTests.cs`

**Assertions:**
- For concurrency levels 10, 100, and 1,000, exactly one logical delivery is accepted and the remaining calls are duplicates.
- Exactly one handler execution occurs for the accepted delivery.
- The result is repeated for in-memory, SQLite/EF In-Memory, Redis when available, and the end-to-end host.
- No test uses timing sleeps; use tasks/barriers and bounded test timeouts.

- [ ] **Step 1: Write parameterized stress tests**

Use a shared barrier, identical provider/Event ID, and independent Webhook IDs. Count only successful claims and handler invocations, not raw response timing.

- [ ] **Step 2: Run the in-memory stress tests and confirm failure or pass**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Concurrency"
```

Expected: the test either fails on the current scaffold or passes only after Task 10 is complete.

- [ ] **Step 3: Add provider-specific stress tests**

Use SQLite/EF In-Memory for routine CI. Run Redis stress tests when `WEBHOOKKIT_REDIS_CONNECTION` is set and otherwise mark the external-service test as skipped with a clear reason.

- [ ] **Step 4: Run all stress tests and verify counts**

Run:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Concurrency"; dotnet test "tests\WebhookKit.EntityFrameworkCore.Tests\WebhookKit.EntityFrameworkCore.Tests.csproj" --filter "FullyQualifiedName~Concurrency"; dotnet test "tests\WebhookKit.Redis.Tests\WebhookKit.Redis.Tests.csproj" --filter "FullyQualifiedName~Concurrency"
```

Expected: one accepted delivery and one handler execution per scenario.

---

### Task 28: Build the Full Integration Test Suite

**Files:**
- Create: `tests/WebhookKit.IntegrationTests/WebhookKitTestApplication.cs`
- Create: `tests/WebhookKit.IntegrationTests/WebhookScenariosTests.cs`
- Modify: `tests/WebhookKit.IntegrationTests/WebhookKit.IntegrationTests.csproj`

**Scenarios:**
1. Valid signed webhook reaches a typed handler and is `Processed`.
2. Tampered signature returns 401.
3. Expired timestamp returns 400.
4. Duplicate returns 200 in sync mode without executing the handler twice.
5. Unregistered EventType is acknowledged and stored as `Ignored`.
6. Malformed JSON returns a safe 400 and no handler execution.
7. Oversized body returns 413.
8. Retryable handler failure retries and eventually succeeds.
9. Retry exhaustion marks `Failed` and returns 500.
10. Async mode returns 202 and the hosted worker processes the persisted record.
11. MVC endpoint has the same behavior as Minimal API.
12. Custom JSON path and DI extractor route a custom event type.

- [ ] **Step 1: Add a test host and handler probes**

Use a test application configured with the in-memory store, fake clock, test provider, deterministic handler counters, and controllable retry exceptions. Make the test host available to both Minimal API and MVC tests.

- [ ] **Step 2: Run the integration project and confirm failure**

Run:

```powershell
dotnet test "tests\WebhookKit.IntegrationTests\WebhookKit.IntegrationTests.csproj"
```

Expected: failure because the project has no test host or endpoint pipeline.

- [ ] **Step 3: Implement all scenarios using the real ASP.NET test server**

Use signed `HttpRequestMessage` instances from `WebhookKit.Testing`, assert response codes and persisted statuses, wait with bounded polling rather than arbitrary sleeps, and assert handler counters.

- [ ] **Step 4: Run the integration suite**

Run:

```powershell
dotnet test "tests\WebhookKit.IntegrationTests\WebhookKit.IntegrationTests.csproj"
```

Expected: all scenarios pass without external services.

---

### Task 29: Implement Runnable Samples

**Files:**
- Modify: `samples/MinimalApi/Program.cs`
- Create: `samples/MinimalApi/README.md` and `samples/MinimalApi/requests.http`
- Modify: `samples/Mvc/Program.cs`
- Create: `samples/Mvc/Controllers/WebhooksController.cs`, `samples/Mvc/README.md`, and `samples/Mvc/requests.http`
- Modify: `samples/Redis/Program.cs`
- Create: `samples/Redis/README.md` and `samples/Redis/requests.http`
- Modify: `samples/EntityFrameworkCore/Program.cs`
- Create: `samples/EntityFrameworkCore/Data/WebhookDbContext.cs`, `samples/EntityFrameworkCore/README.md`, and `samples/EntityFrameworkCore/requests.http`
- Modify: each sample `.csproj` only where required by the implementation.

- [ ] **Step 1: Write sample request files**

Include one valid HMAC request, one duplicate request, and one expected rejection example. Use placeholders for secrets and explain the required environment variables.

- [ ] **Step 2: Implement Minimal API and MVC samples**

Use the same provider name, fake-free configuration reading from environment variables, one typed handler, and a visible response status. MVC must use `[WebhookEndpoint]`, not a duplicate custom pipeline.

- [ ] **Step 3: Implement Redis and EF Core samples**

The Redis sample must document `WEBHOOKKIT_REDIS_CONNECTION`; the EF Core sample must use SQLite, apply `ApplyWebhookConfiguration`, and run the application-created schema setup without shipping package-owned migrations.

- [ ] **Step 4: Build and run each sample**

Run:

```powershell
dotnet build "samples\MinimalApi\MinimalApi.csproj" -c Release
dotnet build "samples\Mvc\Mvc.csproj" -c Release
dotnet build "samples\Redis\Redis.csproj" -c Release
dotnet build "samples\EntityFrameworkCore\EntityFrameworkCore.csproj" -c Release
```

Start each sample with a documented environment, call its `.http` request, verify the response, and stop it cleanly.

---

### Task 30: Rewrite the User README and Documentation

**Files:**
- Modify: `README.md`
- Create or update sample README files from Task 29.
- Create: `Docs/verification.md` and `Docs/configuration.md` if those details do not fit the root README.
- Update: `Docs/tasks/index.html` and all task detail pages.

- [ ] **Step 1: Write a reader-oriented README outline**

Use this order: overview, install packages, one copy-paste Minimal API example, handler example, configuration, provider extraction, MVC, sync/async modes, in-memory storage, Redis, EF Core, testing helpers, response codes, security, troubleshooting, building, contributing, and license.

- [ ] **Step 2: Write the first-run path**

The first-run example must define a payload, handler, `AddWebhookKit`, `AddWebhookHandler`, `MapWebhook`, and `app.Run()`. It must state where the secret comes from, show the expected request, and explain that the provider must send the configured event ID/timestamp/signature headers.

- [ ] **Step 3: Document advanced behavior accurately**

Explain mode-specific 200/202 responses, unknown versus missing event types, raw-body boundary, body limits, queue non-durability, store leases, Redis TTLs, EF migration ownership, retry defaults, and safe problem responses. Do not claim features that are not implemented.

- [ ] **Step 4: Update roadmap status**

Mark Tasks 01–09 as completed based on existing code/tests, then mark each later task only after its tests and verification pass. Keep the dashboard and detailed pages consistent.

- [ ] **Step 5: Review the README as a new user**

Verify every code sample compiles, every package name exists, every command is copy-pasteable, and no secret or raw payload appears in sample output.

---

### Task 31: Complete Package Metadata and Documentation Generation

**Files:**
- Modify: `Directory.Build.props`
- Modify: `Directory.Packages.props`
- Modify: every shippable `.csproj` only when package metadata cannot be inherited.

- [ ] **Step 1: Add release-only documentation and source metadata**

Enable `GenerateDocumentationFile` for Release builds, include the root README as `PackageReadmeFile`, retain the MIT license expression, configure repository metadata, and add the central SourceLink package required for GitHub source indexing.

- [ ] **Step 2: Verify XML documentation coverage**

Run a Release build and ensure public types and methods have XML documentation or an intentional project-level suppression. Do not suppress analyzer warnings.

- [ ] **Step 3: Build all shippable packages**

Run:

```powershell
dotnet build "src\WebhookKit.Abstractions\WebhookKit.Abstractions.csproj" -c Release
dotnet build "src\WebhookKit.Core\WebhookKit.Core.csproj" -c Release
dotnet build "src\WebhookKit.AspNetCore\WebhookKit.AspNetCore.csproj" -c Release
dotnet build "src\WebhookKit.Redis\WebhookKit.Redis.csproj" -c Release
dotnet build "src\WebhookKit.EntityFrameworkCore\WebhookKit.EntityFrameworkCore.csproj" -c Release
dotnet build "src\WebhookKit.Testing\WebhookKit.Testing.csproj" -c Release
```

Expected: all packages build with zero warnings.

---

### Task 32: Add Package Packing and Inspection

**Files:**
- Create: `scripts/validate-packages.ps1`
- Modify: `.gitignore` only if generated package artifacts are not already ignored.

- [ ] **Step 1: Implement package validation**

The script must pack all six source projects into a temporary `artifacts/packages` directory, inspect every `.nupkg` and `.snupkg` ZIP, and fail when expected assemblies, XML docs, README, license, SourceLink metadata, or symbols are missing or when source files, test files, `.env` files, or secret-like files are present.

- [ ] **Step 2: Run the validation script**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File "scripts\validate-packages.ps1" -Configuration Release
```

Expected: all packages pass inspection and no development secret or source file is packaged.

- [ ] **Step 3: Clean generated package outputs**

Remove only the generated `artifacts/packages` directory after inspection; do not remove source or test files.

---

### Task 33: Add CI and Protected Release Workflows

**Files:**
- Create: `.github/workflows/ci.yml`
- Create: `.github/workflows/release.yml`

- [ ] **Step 1: Add the pull-request validation workflow**

Use checkout, setup-dotnet for net8.0/net9.0/net10.0, restore, multi-target Release build, net8.0 tests, pack, and package inspection. Use no secrets in the PR workflow.

- [ ] **Step 2: Add the protected release workflow**

Trigger manually or from a protected release tag, run the same verification, and use NuGet trusted publishing or repository secrets for the publish step. Do not trigger publication from an ordinary push.

- [ ] **Step 3: Validate workflow syntax and local commands**

Run the same restore, build, test, and pack commands locally and inspect the workflow YAML for unpinned action references, credential literals, and accidental publish triggers.

---

### Task 34: Perform the Public API Review

**Files:**
- Create: `Docs/adr/0013-public-api-review.md`
- Modify public contracts only when the review finds a concrete issue.

- [ ] **Step 1: Enumerate the public API**

Inspect all public types in Abstractions, Core, AspNetCore, Redis, EntityFrameworkCore, and Testing. Check naming, nullability, cancellation, default behavior, exception behavior, thread safety, XML documentation, and package references.

- [ ] **Step 2: Write focused API tests**

Add compile-time tests for the documented simple Minimal API, advanced endpoint options, handler registration, store registration, Redis registration, EF Core registration, and testing request builder.

- [ ] **Step 3: Record decisions and remove unnecessary public surface**

Document accepted API decisions in ADR 0013. Make internal implementation types internal when consumers do not need them.

- [ ] **Step 4: Run the full API and package verification**

Run:

```powershell
dotnet build "Ehsan.Webhook.Kit.slnx" -c Release
dotnet test "Ehsan.Webhook.Kit.slnx" -c Release --no-restore
```

Expected: no public API ambiguity, no warning, and no regression.

---

### Task 35: Add Real BenchmarkDotNet Baselines

**Files:**
- Modify: `benchmarks/WebhookKit.Benchmarks/Program.cs`
- Create: `benchmarks/WebhookKit.Benchmarks/WebhookKitBenchmarks.cs`
- Create: `benchmarks/README.md`

- [ ] **Step 1: Define benchmark classes**

Benchmark HMAC-SHA256 verification, HMAC-SHA512 verification, JSON deserialization, in-memory deduplication, and request metadata/context construction. Use preallocated fixed inputs and no secret logging.

- [ ] **Step 2: Document benchmark configuration**

Record target framework, runtime, configuration, iterations, hardware context, and the exact command used to reproduce the baseline.

- [ ] **Step 3: Run a short benchmark**

Run:

```powershell
dotnet run -c Release --project "benchmarks\WebhookKit.Benchmarks\WebhookKit.Benchmarks.csproj" -- --filter "*WebhookKitBenchmarks*"
```

Expected: generated baseline results and no benchmark assertion failures.

- [ ] **Step 4: Review results without score-driven optimization**

Record findings in `benchmarks/README.md`; do not change public APIs or storage semantics solely to improve benchmark numbers.

---

### Task 36: Validate the v1.0.0 Release Candidate

**Files:**
- Modify: `Directory.Build.props` version metadata if the repository does not already set `1.0.0`.
- Create: `Docs/release-candidate-checklist.md`.

- [ ] **Step 1: Set and verify the candidate version**

Use `1.0.0` locally. Verify every shippable package reports the same version and does not contain a prerelease suffix.

- [ ] **Step 2: Run the full quality gate**

Run:

```powershell
dotnet restore "Ehsan.Webhook.Kit.slnx"
dotnet build "Ehsan.Webhook.Kit.slnx" -c Release
dotnet test "Ehsan.Webhook.Kit.slnx" -c Release --no-build
powershell -ExecutionPolicy Bypass -File "scripts\validate-packages.ps1" -Configuration Release
```

Expected: zero failures, zero compiler warnings, zero analyzer warnings, successful tests, and valid packages.

- [ ] **Step 3: Run all sample build checks**

Run the four sample build commands from Task 29 and verify the documented startup commands do not require a secret committed to the repository.

- [ ] **Step 4: Record the release candidate result**

Record commit, SDK, target frameworks, test count, package list, benchmark baseline, sample checks, and known limitations in `Docs/release-candidate-checklist.md`.

---

### Task 37: Prepare External Release Without Publishing

**Files:**
- Modify: `Docs/release-candidate-checklist.md` only to mark the external handoff state.
- Do not create a tag or publish from this plan.

- [ ] **Step 1: Confirm external release is still unauthorized**

Verify the user has not requested a tag, NuGet publication, or remote branch operation. Leave the repository at the validated local candidate state.

- [ ] **Step 2: Provide the release handoff**

Report the exact package versions, artifact paths, validation output, and the protected workflow that will be used after separate authorization. Do not infer permission to publish from the request to complete the kit.

---

## Final Verification Checklist

- [ ] `dotnet restore "Ehsan.Webhook.Kit.slnx"` succeeds.
- [ ] `dotnet build "Ehsan.Webhook.Kit.slnx" -c Release` succeeds with zero warnings.
- [ ] `dotnet test "Ehsan.Webhook.Kit.slnx" -c Release --no-build` passes all tests.
- [ ] `dotnet format "Ehsan.Webhook.Kit.slnx" --verify-no-changes --no-restore` reports no formatting violations, or the repository’s configured formatter equivalent is run and documented.
- [ ] In-memory, EF Core, optional Redis, integration, and security tests pass.
- [ ] Minimal API and MVC scenarios have matching behavior.
- [ ] All four samples build and their documented requests work.
- [ ] `README.md` is a simple, accurate, copy-pasteable user guide.
- [ ] Package validation confirms XML docs, README, license, symbols, SourceLink, and no secrets/source leakage.
- [ ] CI validates restore, build, test, and pack without publishing.
- [ ] Roadmap status reflects completed work and keeps Tasks 01–02 explicitly complete.
- [ ] No tag, publish, remote push, or other external release action occurs without explicit authorization.
