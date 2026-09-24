# WebhookKit Security Audit

**Date:** 2026-09-24

## Conclusion

The implemented WebhookKit production paths passed this internal security audit after three narrow security fixes. No release-blocking defect remains in the audited scope. Release is conditionally acceptable for the in-memory, EF Core, and HTTP paths; deployments that enable Redis should complete real-server integration validation before production rollout.

This is an internal automated and source-level audit. It is not an external penetration test, formal assurance review, or clean external security certification.

## Scope

The audit covered the implemented code in:

- `WebhookKit.Abstractions`
- `WebhookKit.Core`
- `WebhookKit.Testing`
- `WebhookKit.AspNetCore`
- `WebhookKit.Redis`
- `WebhookKit.EntityFrameworkCore`

The review and executable tests covered:

- HMAC-SHA256 and HMAC-SHA512 generation and verification, secret rotation, malformed input, encoding mismatch, and fixed-time comparison.
- Advertised-length and streamed request-body limits.
- Replay-window boundaries, malformed and missing timestamps, explicit opt-out, and cancellation.
- Atomic in-memory deduplication at 10, 100, and 1,000 concurrent callers.
- Minimal API and MVC handling of handler and dependency failures.
- Safe expected outcomes for signature, payload, oversize, and queue failures.
- Sensitive-marker exclusion from logs, activities, acknowledgements, problem responses, safe result strings, and persisted failure diagnostics.
- Redis key hashing, dedicated DTO serialization, and safe failure persistence through the deterministic Redis adapter double.
- EF Core schema mapping, SQLite persistence, and safe failure persistence.
- Worker retry and terminal-failure persistence.

Infrastructure controls, TLS termination, WAF or rate limiting, provider-specific operations, and external penetration testing were outside this task.

## TDD Evidence

### Clean baseline

Before adding security tests:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj"
dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj"
```

- Core baseline: 237 passed, 0 failed, 0 skipped.
- ASP.NET Core baseline: 114 passed, 0 failed, 0 skipped.

### Required RED

The exact two-command checkpoint from the task brief was run before any production edit:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Security"; dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj" --filter "FullyQualifiedName~Security"
```

The first invocation found two test-construction defects: the ASP.NET helper did not compile because it passed `byte[]` to a `string` parameter, and the Core marker test omitted its timestamp header. Those test-only defects were corrected before production changes. The valid RED rerun reported:

- Core security tests: 17 passed, 9 failed, 0 skipped, 26 total.
- ASP.NET Core security tests: 14 passed, 1 failed, 0 skipped, 15 total.

The nine Core failures were:

- Eight HMAC rows across HMAC-SHA256 and HMAC-SHA512 proved that tampered, malformed, wrong-secret, and encoding-mismatch inputs returned distinguishable failure reasons.
- One out-of-range numeric timestamp row threw `ArgumentOutOfRangeException` instead of returning a safe rejection.

The ASP.NET Core failure proved that a 31-byte streamed limit caused 131,072 bytes to be requested before rejection instead of stopping at 32 bytes.

### Focused regressions and fixes

1. HMAC generic rejection:
   - Before: eight rows observed `Signature mismatch.` or `Signature format is invalid.`.
   - Fix: `HmacSignatureVerifier.VerifyAsync` now returns `Signature verification failed.` for malformed and mismatching signatures.
   - After: 8 passed, 0 failed.

2. Out-of-range timestamp:
   - Before: an oversized numeric epoch threw `ArgumentOutOfRangeException`.
   - Fix: only the Unix conversion range failure maps to `Timestamp format is invalid.`.
   - After: 2 passed, 0 failed.

3. Streamed body read-ahead:
   - Before: a 31-byte limit requested and received 131,072 bytes.
   - Fix: pool rental and each read are bounded by the configured limit plus at most one proof byte.
   - After: 1 passed, 0 failed.

No assertion was weakened, skipped, or swallowed.

## Final Verification

The exact security command was green with the final audit suite:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj" --filter "FullyQualifiedName~Security"; dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj" --filter "FullyQualifiedName~Security"
```

- Core security: 28 passed, 0 failed, 0 skipped.
- ASP.NET Core security: 19 passed, 0 failed, 0 skipped.

Full required suites:

```powershell
dotnet test "tests\WebhookKit.Core.Tests\WebhookKit.Core.Tests.csproj"
dotnet test "tests\WebhookKit.AspNetCore.Tests\WebhookKit.AspNetCore.Tests.csproj"
```

- Core: 265 passed, 0 failed, 0 skipped.
- ASP.NET Core: 133 passed, 0 failed, 0 skipped.

Storage-surface suites:

```powershell
dotnet test "tests\WebhookKit.Redis.Tests\WebhookKit.Redis.Tests.csproj"
dotnet test "tests\WebhookKit.EntityFrameworkCore.Tests\WebhookKit.EntityFrameworkCore.Tests.csproj"
```

- Redis deterministic adapter suite: 26 passed, 0 failed, 0 skipped.
- EF Core with SQLite: 22 passed, 0 failed, 0 skipped.

Release solution build:

```powershell
dotnet build "Ehsan.Webhook.Kit.slnx" -c Release
```

- Build succeeded.
- 0 warnings.
- 0 errors.
- All production target frameworks in the solution built for `net8.0`, `net9.0`, and `net10.0` where configured.

## Audit Matrix

| Control | Executable evidence | Implementation evidence | Result |
|---|---|---|---|
| HMAC-SHA256 and HMAC-SHA512 use fixed-time equality | `WebhookSecurityTests.HmacVerifier_AllAlgorithmsAndFailureModes_ReturnOneIndistinguishableSafeResult`; `SignatureGenerator_AllAlgorithmsAndFailureModes_ReturnOneIndistinguishableSafeResult` | `HmacSignatureVerifier.VerifyAsync` calls `CryptographicOperations.FixedTimeEquals`; `WebhookSignatureGenerator.VerifyBytes` calls it for every verification secret | Verified |
| Tampered, malformed, wrong-secret, and encoding-mismatch results are externally indistinguishable | Eight Core rows per HMAC implementation plus four real Minimal API TestServer requests with identical status, problem code/message, and log shape | Core verifier now has one safe failure reason; endpoint maps all signature rejection to one code | Verified |
| Secret rotation remains valid | Existing HMAC tests plus configured current and rotation markers in security tests | `HmacSignatureVerifier.VerifyAsync` and `WebhookSignatureGenerator.VerifyBytes` evaluate configured secret collections | Verified |
| Advertised oversize is rejected before reading or body allocation | `ReadRawBodyAsync_SecurityAdvertisedOversizeRejectsBeforeReadingOrBackingTheBody` | `WebhookBodyReader.ReadRawBodyAsync` checks `Content-Length` before buffering | Verified |
| Streamed oversize is bounded | `ReadRawBodyAsync_SecurityStreamedOversizeStopsAtOneByteBeyondLimitAndBoundsEveryRead` | `WebhookBodyReader.ReadRawBodyAsync` caps pool rental and read length to the remaining limit plus one byte | Verified |
| Replay boundary is exact in both directions | `TimestampVerifier_ExactToleranceBoundaries_AcceptsBoundaryAndRejectsOneTickOutside` | `WebhookTimestampVerifier.VerifyAsync` rejects only `skew > tolerance` | Verified |
| Malformed or missing timestamps are rejected unless missing is explicitly allowed | `TimestampVerifier_OutOfRangeNumericTimestamp_ReturnsSafeRejection`, `TimestampVerifier_MissingHeader_RequiresExplicitOptOut`, and `TimestampVerifier_MalformedHeader_IsRejectedEvenWhenMissingIsAllowed` | `WebhookTimestampVerifier.VerifyAsync` validates range and applies `AllowMissing` only to absent/blank values | Verified |
| Cancellation propagates | `TimestampVerifier_CancelledToken_PropagatesCancellation` plus existing ingestion, retry, and body-reader cancellation tests | Verifier checks cancellation before verification; pipeline preserves `OperationCanceledException` | Verified |
| Deduplication is atomic at 10, 100, and 1,000 callers | `Deduplication_SharedBarrierAtEachScale_AllowsOneWinnerAndOneRecord` uses one shared async barrier and no sleeps | `InMemoryWebhookStore.TryCreateAsync` performs the check and insert under `_createGate` | Verified |
| Handler and dependency failures do not escape Minimal API or MVC | Four real TestServer rows in `RealTestServerPaths_HandlerAndDependencyFailures_DoNotEscapeAsUnhandledServerErrors` | `WebhookProcessor.DispatchAsync` converts handler exceptions to safe dispatch failures; `WebhookEndpointService` writes safe results; MVC short-circuits unsuccessful outcomes | Verified |
| Expected signature, payload, oversize, and queue outcomes stay safe | Eight real Minimal API/MVC TestServer rows | Endpoint maps outcomes to fixed 401, 400, 413, and 503 problem responses | Verified |
| Sensitive markers do not reach logs, state, activities, responses, or safe strings | Core marker tests and real endpoint tests inject unique raw-body, Authorization, signature, configured-secret, rotation-secret, verifier-reason, parser-detail, handler/dependency-message, and stack-text markers | `WebhookLogMessages`, `WebhookEndpointLogMessages`, and `WebhookDiagnostics` define bounded safe fields; response writer serializes only `WebhookProblemResponse`; result `ToString` methods expose status/code only | Verified |
| Success acknowledgements do not echo payloads | Four real Minimal API/MVC synchronous/asynchronous acknowledgement rows | Response writer returns immediately for success; no raw request values are written | Verified |
| Redis keys do not expose provider/Event ID or body hash | `RedisWebhookStoreTests.Keys_HashProviderScopedDeduplicationIdentityAndValidateWebhookId` | `RedisWebhookStore.BuildDeduplicationIdentityHash` uses SHA-256 before key construction | Verified against deterministic adapter |
| Redis JSON is a dedicated DTO without options, connection strings, exceptions, or configured secrets | `RedisWebhookStoreTests.Serialization_ContainsOnlyDedicatedWebhookRecordDtoFields` | `RedisWebhookRecordDto` is the only serialized record type; store options are constructor snapshots and are not serialized | Verified against deterministic adapter |
| EF persistence uses a dedicated entity and sanitizes failure diagnostics | `ApplyWebhookConfiguration_MapsKeysIndexesStorageAndConcurrency`, `RoundTrip_PreservesMetadataBodyCorrelationAttemptsAndSafeFailureFields`, and `MarkFailedAsync_RequiresCurrentOwnerAndSanitizesFailureReason` | `WebhookEntity` has no options, connection, or exception member; `EfCoreWebhookStore` serializes only headers to JSON and normalizes unsafe failure text | Verified with SQLite |
| Worker and retry persistence do not retain exception details | Existing Core retry and worker suites, including retry exhaustion and unknown-exception rows | `WebhookRetryExecutor` normalizes to safe codes; `WebhookBackgroundWorker.ResolveFailureCode` accepts only bounded code characters | Verified |

## Implementation Evidence

### Security fixes

- `src/WebhookKit.Core/Verifiers/HmacSignatureVerifier.cs`
  - `VerifyAsync` uses one failure reason for malformed and mismatching signatures.
  - HMAC comparison remains `CryptographicOperations.FixedTimeEquals` at line 136 after the audit edits.
- `src/WebhookKit.Core/Verifiers/WebhookTimestampVerifier.cs`
  - `VerifyAsync` maps only `ArgumentOutOfRangeException` from Unix conversion to the existing safe invalid-format result.
- `src/WebhookKit.AspNetCore/WebhookBodyReader.cs`
  - `ReadRawBodyAsync` performs the advertised-length fast rejection, caps the initial pooled rental, and caps every streamed read to the remaining allowance plus one byte.
- `tests/WebhookKit.Core.Tests/HmacSignatureVerifierTests.cs`
  - Legacy malformed and wrong-secret assertions now require the same exact generic reason.

### Audited HMAC paths

A source search found exactly two production HMAC comparison paths in the requested assemblies:

- `src/WebhookKit.Core/Verifiers/HmacSignatureVerifier.cs`: `CryptographicOperations.FixedTimeEquals` at line 136.
- `src/WebhookKit.Testing/WebhookSignatureGenerator.cs`: `CryptographicOperations.FixedTimeEquals` at line 375.

`WebhookKit.Abstractions` contains contracts and result values but no HMAC comparison implementation. Both algorithms use `HMACSHA256.HashData` or `HMACSHA512.HashData`; neither path compares signatures with ordinary string or sequence equality.

### Audited safe output boundaries

- `src/WebhookKit.Core/Diagnostics/WebhookLogMessages.cs` logs only Webhook/provider/event/status/attempt/trace/correlation identifiers and bounded failure codes.
- `src/WebhookKit.AspNetCore/Diagnostics/WebhookEndpointLogMessages.cs` adds only endpoint status/outcome/mode and the same bounded identifiers.
- `src/WebhookKit.Core/Diagnostics/WebhookDiagnostics.cs` defines only Webhook ID, provider, Event ID, Event Type, status, and correlation tags.
- `src/WebhookKit.AspNetCore/Responses/WebhookResponseWriter.cs` writes only the safe problem DTO for failures and no body for success.
- `src/WebhookKit.AspNetCore/Pipeline/WebhookEndpointResult.cs`, `src/WebhookKit.Core/Processing/WebhookIngestionResult.cs`, and `src/WebhookKit.Core/Processing/WebhookDispatchResult.cs` return status/code-only `ToString` values.

### Audited persistence boundaries

- `src/WebhookKit.Redis/RedisWebhookStore.cs`
  - Deduplication key material is SHA-256 hashed before key construction.
  - `RedisWebhookRecordDto` is a dedicated record DTO.
  - Failure text is normalized to a bounded safe code.
- `src/WebhookKit.EntityFrameworkCore/WebhookEntity.cs`
  - The mapped entity contains webhook record data only; it has no options, connection, or exception member.
- `src/WebhookKit.EntityFrameworkCore/EfCoreWebhookStore.cs`
  - Header JSON is the only JSON payload.
  - Unsafe failure text becomes `processing-failed`.
- `src/WebhookKit.Core/Workers/WebhookBackgroundWorker.cs`
  - Retry and terminal persistence use bounded safe failure codes and do not attach exception objects to logs or records.

## Known Limitations and Operational Caveats

1. **No real Redis execution evidence:** Task 23 used a deterministic stateful adapter double. This audit did not start or contact Redis. Lua execution, Redis `cjson`, `SET KEEPTTL`, TTL precision, sorted-set score behavior, keyspace behavior, and Redis Cluster slot co-location were not exercised against a real Redis server. This audit makes no claim that those behaviors were executed against Redis.
2. **In-process queue durability:** `ChannelWebhookQueue` is a bounded in-process notification channel. A process restart can lose a notification. Persisted records remain recoverable, but queue delivery itself is not durable across restarts.
3. **Persistence is intentionally sensitive:** Redis and EF record persistence can retain the raw body and captured request headers, including Authorization or signature header values, according to storage configuration. Datastore encryption, access control, retention, and redaction remain deployment responsibilities.
4. **Exception objects remain exceptions:** Handler, parser, and dependency exception messages and stacks naturally exist on the in-process exception objects used to classify failures. The tests prove they are not copied to the audited logs, activities, responses, safe result strings, or persisted failure fields.
5. **Constant-time evidence is implementation-level:** The source audit confirms the required cryptographic primitive at both comparison sites and behavior tests cover all failure modes. This is not a statistical timing measurement or side-channel laboratory assessment.
6. **External assessment not performed:** No independent penetration test, external code review, certification, live provider exercise, or production infrastructure assessment was performed.

## Release Conclusion

No known release-blocking security defect remains in the implemented and tested scope after the three focused fixes. The automated suites and Release build support a conditional internal release approval. If the Redis store is shipped, real Redis integration and operational validation remain required before production use. The in-process queue durability limitation and sensitive datastore handling must remain documented deployment constraints.

This conclusion is an internal engineering release decision, not a claim of clean external security certification.
