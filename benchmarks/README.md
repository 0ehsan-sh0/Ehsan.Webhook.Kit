# WebhookKit Benchmark Baselines

This directory contains the permanent BenchmarkDotNet coverage suite for delivered WebhookKit seams. It records current behavior rather than comparing optimization alternatives. The suite does not change production APIs, storage semantics, or implementation code for a score.

## Cases

| Case | Delivered seam | Coverage |
| --- | --- | --- |
| `VerifyHmacSha256` | `IWebhookSignatureVerifier` resolved from `AddWebhookKit` | Real production HMAC-SHA256 verification over fixed raw bytes and headers |
| `VerifyHmacSha512` | `IWebhookSignatureVerifier` resolved from `AddWebhookKit` | Real production HMAC-SHA512 verification over fixed raw bytes and headers |
| `DeserializeJson` | `IWebhookDeserializer` resolved from `AddWebhookKit` | System.Text.Json typed deserialization with fixed payload and serializer options |
| `AtomicCreateAccepted` | `IWebhookStore.TryCreateAsync` | Real in-memory atomic create path using prebuilt unique records |
| `AtomicCreateDuplicate` | `IWebhookStore.TryCreateAsync` | Real in-memory duplicate-rejection path against a preinserted record |
| `ConstructVerificationContext` | `WebhookVerificationContext` | Provider, raw-body reference, and safe header snapshot construction |
| `ConstructWebhookContext` | `WebhookContext` | Metadata, intentional raw-body defensive copy, and safe header snapshot construction |

Signature inputs use fixed preallocated bytes, headers, and signatures. The private fixture secret is used only to prepare the setup inputs; it is not a benchmark parameter and is not written to reports or logs. No HTTP hosting is benchmarked.

The accepted-create case declares 64 BDN invocations per iteration. Each invocation performs one real atomic store admission. `[IterationSetup]` creates a fresh DI-resolved real in-memory store before the iteration, and the prebuilt unique records keep the accepted path from changing into duplicate rejection. Setup and state reset are outside the measured region. `OperationsPerInvoke` is intentionally not used because each invocation is one store operation; BDN repeats the real operation and reports per-operation values. The duplicate case is preloaded once and remains stable across invocations. There are no manual benchmark loops or timing sleeps.

## Configuration

- Project target: `net8.0`
- Configuration: `Release`
- BenchmarkDotNet: `0.14.0`, supplied by the existing CPM entry
- Job: `[ShortRunJob]`
- Final run: `LaunchCount=1`, `IterationCount=3`, `WarmupCount=3`
- Default cases: BDN-selected invocation count and `UnrollFactor=16`
- Accepted-create case: `InvocationCount=64`, `UnrollFactor=1` because iteration setup resets the store
- Diagnoser: `MemoryDiagnoser`
- Exporters: GitHub-flavored Markdown, CSV, and full JSON
- Entry point: `BenchmarkSwitcher.FromAssembly(...).Run(args, config)` with forwarded CLI arguments
- The benchmark project adds only the unversioned `Microsoft.Extensions.DependencyInjection` package reference required to build the public DI seam; no versioned duplicate reference was added.

## Reproduction commands

Run these from the repository root in PowerShell.

Discovery:

```powershell
dotnet run -c Release --no-build --project "benchmarks\WebhookKit.Benchmarks\WebhookKit.Benchmarks.csproj" -- --list flat
```

Dry validation, run after a Release build:

```powershell
dotnet run -c Release --no-build --project "benchmarks\WebhookKit.Benchmarks\WebhookKit.Benchmarks.csproj" -- --job Dry --filter "*WebhookKitBenchmarks*" --noOverwrite
```

The final plan command, intentionally without an extra job flag:

```powershell
dotnet run -c Release --project "benchmarks\WebhookKit.Benchmarks\WebhookKit.Benchmarks.csproj" -- --filter "*WebhookKitBenchmarks*"
```

With BenchmarkDotNet 0.14.0, the Dry flag adds a Dry job to the source `[ShortRunJob]`, so the Dry validation log contains 14 executions: seven Dry cases and seven ShortRun cases. The final command contains and executes seven cases.

## Measured baseline

The final report was produced by BenchmarkDotNet v0.14.0 on Windows 11 build `10.0.26200.8973`, with an Intel Core i7-8550U CPU at 1.80 GHz, one CPU, eight logical/four physical cores, .NET SDK `10.0.401`, and the target runtime `.NET 8.0.31 (8.0.3126.42015), X64 RyuJIT AVX2`. The report identifies Concurrent Workstation GC.

| Method | Mean | Error | Allocated |
| --- | ---: | ---: | ---: |
| `VerifyHmacSha256` | 3,730.5 ns | 24,146.2 ns | 312 B |
| `VerifyHmacSha512` | 2,586.0 ns | 132.9 ns | 344 B |
| `DeserializeJson` | 574.9 ns | 622.9 ns | 240 B |
| `AtomicCreateDuplicate` | 886.4 ns | 1,129.5 ns | 1,384 B |
| `ConstructVerificationContext` | 396.3 ns | 443.3 ns | 640 B |
| `ConstructWebhookContext` | 665.8 ns | 1,369.4 ns | 1,416 B |
| `AtomicCreateAccepted` | 2,856.8 ns | 8,277.0 ns | 1,741 B |

`Error` is the half-width of BenchmarkDotNet's 99.9% confidence interval. `Allocated` is managed allocation per operation from `MemoryDiagnoser`.

## Interpretation and caveats

These are reproducible current-behavior baselines, not cross-machine guarantees. The final job intentionally uses only three measurement iterations, so the error column is wide for several short operations; use a longer job before making performance decisions. The suite does not compare fake implementations or arbitrary method alternatives.

The HMAC cases include the production verifier's provider lookup, raw-body handling, signature decoding, hash computation, and constant-time comparison. The JSON case includes the fixed configured `JsonSerializerOptions`. Store cases include the delivered in-memory store's validation, snapshotting, and atomic admission behavior. Context cases include header snapshotting and, for `WebhookContext`, the intentional raw-body defensive copy. HTTP hosting is intentionally excluded.

The measured report is not an optimization exercise: no public API, storage semantics, or production implementation was changed to improve any score. Treat later changes as comparable only when run with the same target, job, inputs, and machine context.

## Artifacts

BenchmarkDotNet artifacts are ignored by the repository's `BenchmarkDotNet.Artifacts/` rule. The final GitHub Markdown report is at:

`BenchmarkDotNet.Artifacts\results\WebhookKit.Benchmarks.WebhookKitBenchmarks-report-github.md`

The same run also produces the full JSON and CSV reports in that directory.
