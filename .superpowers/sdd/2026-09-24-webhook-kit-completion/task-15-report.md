# Task 15 Report

## Status and commits

Implemented Task 15: the provider-neutral shared ingestion admission path, Minimal API endpoint adapter, route extensions, safe endpoint result contract, queue contracts, net8-compatible clock-backed ULID IDs, per-provider body limits, signing-input policy, explicit timestamp policy, and raw-body storage policy.

Implementation/test commit:

- `a600b44 feat(aspnetcore): add webhook endpoint pipeline`

The required report is committed separately after this file is written. No remote operation was performed.

## Architecture

- `WebhookEndpointService` is the ASP.NET Core adapter. It selects the provider/global body limit, reads the body once, snapshots headers, creates the Webhook ID, invokes the shared Core pipeline, performs async queue admission, maps outcomes to safe results, and completes the HTTP status.
- `WebhookIngestionService` remains provider-neutral and has no ASP.NET Core types. `IngestAsync` remains compatible and now shares admission with `AdmitAsync`; admission verifies, extracts, deduplicates, persists `Received`, creates context, and does not dispatch.
- No channel implementation, worker, retry engine, final response formatter, logging, MVC filter, or `ActivitySource` was added.
- Queue admission occurs only after the unique record is persisted. Missing/full queue results leave the record recoverable.

## Files

### Created task files

- `src/WebhookKit.AspNetCore/Pipeline/WebhookEndpointOptions.cs`
- `src/WebhookKit.AspNetCore/Pipeline/WebhookEndpointService.cs`
- `src/WebhookKit.AspNetCore/DependencyInjection/WebhookEndpointRouteBuilderExtensions.cs`
- `tests/WebhookKit.AspNetCore.Tests/WebhookEndpointServiceTests.cs`

### Required additional contracts and policy files

- `src/WebhookKit.AspNetCore/Pipeline/IWebhookEndpointService.cs`
- `src/WebhookKit.AspNetCore/Pipeline/WebhookEndpointResult.cs`
- `src/WebhookKit.Abstractions/IWebhookIdGenerator.cs`
- `src/WebhookKit.Abstractions/IWebhookQueue.cs`
- `src/WebhookKit.Abstractions/WebhookWorkItem.cs`
- `src/WebhookKit.Core/Clocks/WebhookIdGenerator.cs`
- `src/WebhookKit.Core/Options/WebhookSignatureInput.cs`
- `src/WebhookKit.Core/Options/WebhookStorageOptions.cs`

### Modified task files

- `src/WebhookKit.AspNetCore/DependencyInjection/WebhookKitAspNetCoreServiceCollectionExtensions.cs`
- `src/WebhookKit.Abstractions/WebhookRecord.cs`
- `src/WebhookKit.Core/DependencyInjection/WebhookKitServiceCollectionExtensions.cs`
- `src/WebhookKit.Core/Options/WebhookKitOptions.cs`
- `src/WebhookKit.Core/Options/WebhookKitOptionsValidator.cs`
- `src/WebhookKit.Core/Options/WebhookProviderOptions.cs`
- `src/WebhookKit.Core/Options/WebhookSignatureOptions.cs`
- `src/WebhookKit.Core/Options/WebhookTimestampOptions.cs`
- `src/WebhookKit.Core/Processing/WebhookIngestionService.cs`
- `src/WebhookKit.Core/Verifiers/HmacSignatureVerifier.cs`
- `src/WebhookKit.Core/Verifiers/WebhookTimestampVerifier.cs`

### Modified regression tests

- `tests/WebhookKit.Core.Tests/HmacSignatureVerifierTests.cs`
- `tests/WebhookKit.Core.Tests/WebhookKitOptionsTests.cs`
- `tests/WebhookKit.Core.Tests/WebhookProcessorTests.cs`
- `tests/WebhookKit.Core.Tests/WebhookTimestampVerifierTests.cs`

No plan, ADR, `CONTEXT.md`, progress ledger, README, sample, queue implementation, worker, or response-formatter file was modified.

## TDD RED evidence

The required test-quality reference was read before production edits. The endpoint behavior tests were added first, then the exact command was run:

```powershell
dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj" --filter "FullyQualifiedName~Endpoint"
```

The clean RED run failed during compilation before test execution, as expected, because the Task 15 surface did not exist. Observed diagnostics included:

- `CS0234`: `WebhookKit.AspNetCore.Pipeline` did not exist.
- `CS0246`: `IWebhookQueue` did not exist.
- `CS0246`: `WebhookStorageOptions` did not exist.
- `CS0246`: `WebhookProcessingMode` did not exist.
- `CS0246`: `WebhookEndpointOptions` did not exist.
- `CS0246`: `WebhookWorkItem` did not exist.

A first RED attempt also found a test-fixture accessibility typo; that test-only issue was corrected before the recorded clean RED run. No production code existed when the clean RED command was run.

## GREEN and final verification

Focused endpoint command after implementation:

```powershell
dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj" --filter "FullyQualifiedName~Endpoint"
```

Result: 32 passed, 0 failed, 0 skipped.

Required full ASP.NET Core command:

```powershell
dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj"
```

Result: 39 passed, 0 failed, 0 skipped. This includes the 7 pre-existing body-reader tests and 32 endpoint tests.

Required affected Core command:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj"
```

Result: 125 passed, 0 failed, 0 skipped.

Solution regression command:

```powershell
dotnet test "Ehsan.Webhook.Kit.slnx" --configuration Debug --verbosity minimal
```

Discovered tests passed: Abstractions 16/16, Core 125/125, ASP.NET Core 39/39. Redis, EF Core, and Integration test projects currently contain no discovered tests.

Release builds, each covering `net8.0`, `net9.0`, and `net10.0`:

```powershell
dotnet build "src\WebhookKit.Abstractions\WebhookKit.Abstractions.csproj" --configuration Release
dotnet build "src\WebhookKit.Core\WebhookKit.Core.csproj" --configuration Release
dotnet build "src\WebhookKit.AspNetCore\WebhookKit.AspNetCore.csproj" --configuration Release
```

Each completed with 0 warnings and 0 errors. `git diff --check` was clean.

## Endpoint outcome table

| Scenario | Result/status | Evidence |
|---|---:|---|
| Valid synchronous delivery | `Processed`, 200 | `ProcessAsync_ValidSynchronousRequest_ProcessesAndPersistsTerminalRecord` |
| Invalid signature | `InvalidSignature`, 401 | `ProcessAsync_InvalidSignature_ReturnsFixedUnauthorizedResultWithoutVerifierReason` |
| Expired timestamp | `InvalidTimestamp`, 400 | `ProcessAsync_ExpiredTimestamp_ReturnsBadRequestWithoutDisclosingReason` |
| Missing timestamp request header | `InvalidTimestamp`, 400 | `ProcessAsync_MissingTimestamp_ReturnsBadRequestWhenProtectionIsNotOptedOut` |
| Missing timestamp configuration | options validation failure | `ProcessAsync_MissingTimestampConfiguration_FailsValidation` plus Core validator tests |
| Missing Event ID | `MissingEventId`, 400 | `ProcessAsync_MissingEventId_ReturnsBadRequest` |
| Missing Event Type | `MissingEventType`, 400 | `ProcessAsync_MissingEventType_ReturnsBadRequest` |
| Unknown Event Type | `Ignored`, 200 | `ProcessAsync_UnknownEventType_IsAcknowledgedAndPersistedAsIgnored` |
| Duplicate synchronous | `Duplicate`, 200 | `ProcessAsync_DuplicateRequest_ReturnsSuccessWithoutDispatchingAgain` |
| Duplicate asynchronous | `Duplicate`, 202 | `ProcessAsync_AsynchronousDuplicate_ReturnsAcceptedWithoutReenqueue` |
| Async admission | `Accepted`, 202 | `ProcessAsync_AsynchronousAdmission_ReturnsAcceptedAndQueuesOnlyReference` |
| Queue missing | `QueueUnavailable`, 503 | `ProcessAsync_AsynchronousAdmissionWithoutQueue_ReturnsServiceUnavailableAndLeavesRecordRecoverable` |
| Queue full | `QueueUnavailable`, 503 | `ProcessAsync_AsynchronousAdmissionWithFullQueue_ReturnsServiceUnavailableAndLeavesRecordRecoverable` |
| Body too large | `PayloadTooLarge`, 413 | `ProcessAsync_OversizedBody_ReturnsPayloadTooLarge` |
| Payload failure | `PayloadInvalid`, 400 | `ProcessAsync_PayloadFailure_ReturnsBadRequestWithoutParserDetails` |
| Handler/processing failure | `ProcessingFailed`, 500 | `ProcessAsync_ProcessingFailure_ReturnsSafeServerError` |
| Cancellation | `OperationCanceledException` | `ProcessAsync_CancelledToken_PropagatesCancellation` |
| Status customization | override only | `ProcessAsync_StatusCodeOverride_OnlyChangesStatus` |

## Verification-order proof

- `WebhookEndpointService` reads the body once at `src/WebhookKit.AspNetCore/Pipeline/WebhookEndpointService.cs:72-102`, then passes exact bytes into `WebhookIngestionRequest`.
- `WebhookIngestionService.AdmitCoreAsync` invokes signature verification at `src/WebhookKit.Core/Processing/WebhookIngestionService.cs:319-343`, checks its result, invokes timestamp/replay verification at `:345-369`, then invokes Event ID and Event Type extraction at `:371-409`.
- The record is created and atomically acquired at `:442-481`; context creation occurs only after acquisition at `:483-493`.
- Synchronous dispatch and terminal persistence occur only after admission in `IngestAsync` at `:220-300`.
- The order test `ProcessAsync_VerificationPrecedesExtractionAndDoesNotDeserializeBeforeVerification` records `signature`, `timestamp`, `event-id`, `event-type`, and proves the deserializer was not called.
- JSON extraction/deserialization therefore cannot precede successful signature and timestamp verification.

## ID, signing, timestamp, and storage evidence

### IDs

- `IWebhookIdGenerator` is provider-neutral and registered with `TryAddSingleton` in Core DI.
- `WebhookIdGenerator` uses `IWebhookClock`, 48-bit Unix milliseconds, 80 bits from `RandomNumberGenerator`, and Crockford base32 encoding.
- `IdGenerator_UsesClockForUlidTimestampPrefix` proves the 26-character alphabet/shape, clock-derived prefix `01M39MJVG`, and clock changes affecting the prefix.
- Endpoint tests prove generated IDs are unique across requests and are persisted as record IDs.
- The implementation has no net9-only API dependency and builds for net8, net9, and net10.

### Signing input

- `WebhookSignatureInput.RawBody` is the default, preserving existing behavior.
- `TimestampPrefixedRawBody` hashes the exact configured timestamp header text, configured UTF-8 separator, and exact raw body bytes.
- `ProcessAsync_TimestampPrefixedSignature_UsesExactTimestampBytesAndConfiguredSeparator` covers exact whitespace preservation and a custom separator.
- `ProcessAsync_TimestampPrefixedSignature_UsesDefaultSeparator` covers the default `.` separator.
- Existing constant-time comparison, hex/base64, SHA-256/SHA-512, and secret rotation remain active; the additional-secret-without-primary regression is covered.

### Timestamp/replay policy

- A missing timestamp `HeaderName` now fails validation and verification unless `AllowMissing=true` is explicitly configured.
- `AllowMissing=true` remains an explicit opt-out for providers without a usable timestamp header.
- Timestamp-prefixed signatures require a configured timestamp header.
- Expired, missing, malformed, and opt-out behavior is covered by Core and endpoint tests.

### Storage policy

- `WebhookStorageOptions.PersistRawBody` defaults to `true`.
- `DiscardRawBodyAfterSuccessfulSync` defaults to `false` and can discard raw bytes after successful synchronous processing.
- Synchronous processing still receives the exact body through `WebhookContext` when persistence is disabled; the record alone has `RawBody=null`.
- Asynchronous admission rejects disabled raw-body persistence before body read/dedup, and default async records remain `Received` with raw bytes available for recovery.
- `WebhookRecord.RawBody` is settable only to support the narrow post-success discard transition; store implementations still copy records.

## Queue behavior and contracts

- `WebhookWorkItem` exposes only validated `WebhookId` and `Provider`; it has no raw body, record, headers, or mutable delivery payload.
- `IWebhookQueue` exposes non-blocking `TryEnqueueAsync`, cancellation-aware blocking `DequeueAsync`, and graceful `CompleteAsync`.
- The endpoint resolves the optional queue only after admission persistence.
- Missing or full queue returns safe 503 while the record remains `Received` and recoverable.
- Successful enqueue returns 202; duplicate async admission returns 202 without re-enqueue.
- No channel implementation or registration was added; Task 20 remains the owner.

## Routing metadata and DI compatibility

- `MapWebhook(pattern, providerName)` maps a POST route with synchronous defaults and visible schema metadata.
- Advanced options overloads support `WebhookEndpointOptions`, mode, operation ID, summary, description, tags, schema inclusion, and status-code-only response customization.
- Route options are snapshotted before mapping; metadata exposes the provider and the snapshot.
- Metadata includes POST method metadata, response status metadata, endpoint name/summary/description, and `ExcludeFromDescription` when schema visibility is disabled.
- Actual route execution is covered by `MapWebhook_PostRouteCompletesWithEndpointStatus`.
- `AddWebhookKitAspNetCore()` and `AddWebhookAspNetCore()` register the endpoint service and body reader through `TryAdd`.
- Existing `AddWebhookBodyReader()` remains available and custom body-reader registrations are preserved.
- Existing Core `IngestAsync`, verifier, extractor, store, deduplicator, and processor seams remain intact; custom ID/verifier/body-reader registrations remain replaceable.

## Self-review

- Confirmed Core contains no `Microsoft.AspNetCore` or `HttpContext` dependency.
- Confirmed no ASP.NET types entered `WebhookIngestionService`.
- Confirmed raw body, headers, signatures, secrets, verifier reasons, parser details, and exception messages are not placed in endpoint result text or `ToString()`.
- Confirmed all expected cancellation paths rethrow `OperationCanceledException`.
- Confirmed verification, extraction, deduplication, context, sync dispatch, and async admission order.
- Confirmed queue failure occurs after persistence and does not delete work.
- Confirmed no code comments were added, no forbidden plan/ADR/glossary/ledger files were touched, and no push/remote operation occurred.
- Confirmed staged paths were task-related only and the staged diff passed `git diff --cached --check`.

## Concerns and deferred work

- Task 17 still owns the formal problem-response formatter/writer; Task 15 intentionally returns safe status-only results and does not emit response bodies.
- Task 16 still owns MVC attribute/filter support; the shared endpoint result/context contract is ready for it.
- Task 20/21 still own the bounded channel, worker, recovery loop, and retry behavior; async endpoints intentionally require an `IWebhookQueue` implementation.
- No standalone lint script is configured in this repository; Release builds with the repository analyzers completed with zero warnings.
