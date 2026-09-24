# WebhookKit v1.0.0 Release Candidate Checklist

## Candidate identity

- **Conclusion status:** locally validated candidate ready for final comprehensive review and external handoff; not published.
- **UTC date:** 2026-09-24.
- **Local date:** 2026-09-24.
- **Candidate source commit:** `6543d46e1742460e2feb16381bcf51dbae5c860b` (`test(redis): add live release-gate coverage`).
- **Checklist commit strategy:** the candidate source commit above is the final source commit before this checklist-only change. The checklist and roadmap status are committed together in the immediately following documentation commit; no implementation-plan checklist is edited. The ignored Task 36 report records the resulting documentation commit hash.
- **Branch:** `feat/webhook-kit-completion`.
- **Release actions:** no tag, package publication, remote push, or external release operation was performed.

The live gate exposed and corrected one internal validation defect before the candidate commit: Redis rejected generator-compatible ULID characters `S` and `W`. A focused live regression failed before the correction and passed afterward. The correction changes no public API and is limited to accepting the already-documented canonical ULID alphabet.

## Environment

| Item | Evidence |
| --- | --- |
| OS | Microsoft Windows 11 Enterprise, version 10.0.26200, build 26200, x64 |
| CPU/hardware | Intel Core i7-8550U at 1.80 GHz; 8 logical processors; 11.88 GB visible memory |
| SDK | .NET SDK 10.0.401; MSBuild 18.9.11 |
| Runtimes | .NET 8.0.31, 9.0.20, and 10.0.12 installed; Release tests execute on net8.0 |
| Docker | Docker Engine 29.7.2 |
| Package target frameworks | `net8.0`, `net9.0`, `net10.0` |
| Test/sample target framework | `net8.0` |
| Local timezone | Iran Standard Time, UTC+03:30 |

## Version and package set

`Directory.Build.props` already supplied `Version=1.0.0`; no version change was made. The six shippable projects inherit that version, and package validation found no prerelease suffix.

| Package ID | Version | Library artifact | Symbol artifact |
| --- | --- | --- | --- |
| `WebhookKit.Abstractions` | `1.0.0` | `WebhookKit.Abstractions.1.0.0.nupkg` (`59892` bytes, SHA-256 `ce963d0ae1bea325fc0841bdfae7e15142680aa51c2b102aca2a0d17f173b4fa`) | `WebhookKit.Abstractions.1.0.0.snupkg` (`32458` bytes, SHA-256 `bcc39d239b5d80aa051df77bda0492ae4eb84fbd001a48f08ef10f2790f0b740`) |
| `WebhookKit.Core` | `1.0.0` | `WebhookKit.Core.1.0.0.nupkg` (`205465` bytes, SHA-256 `fa0c514c9cf09a48f4c65b6a9ed3423e9d7a4b1db15d5eb5bdf7db8ae8fcb684`) | `WebhookKit.Core.1.0.0.snupkg` (`78773` bytes, SHA-256 `27c1ebd3465b77f4fd53ba0986f2ced12502ac158ad1f883ab5d51a13bd17943`) |
| `WebhookKit.AspNetCore` | `1.0.0` | `WebhookKit.AspNetCore.1.0.0.nupkg` (`98698` bytes, SHA-256 `9c86cfd33c522774372ed10dec1683469415dbb0b7de21a93fbca5709d2a2ab9`) | `WebhookKit.AspNetCore.1.0.0.snupkg` (`61102` bytes, SHA-256 `8fb160464a52ff429dbd591fce1a60b6f1cd07643608d229ac3c9f653e0a78f4`) |
| `WebhookKit.EntityFrameworkCore` | `1.0.0` | `WebhookKit.EntityFrameworkCore.1.0.0.nupkg` (`95425` bytes, SHA-256 `26b3e0167f991f11a4f48e019258897b57979e8957078bc19d62639a87878765`) | `WebhookKit.EntityFrameworkCore.1.0.0.snupkg` (`37583` bytes, SHA-256 `b1d361c1602883c5cd4d871cca715f4fe51b9da80bb2b6656f2c5678da14d756`) |
| `WebhookKit.Redis` | `1.0.0` | `WebhookKit.Redis.1.0.0.nupkg` (`86272` bytes, SHA-256 `82cc1e93b39b0f313f9d23d92f6f2b346c59e21fd791f09208c8c08972340b9e`) | `WebhookKit.Redis.1.0.0.snupkg` (`35966` bytes, SHA-256 `4dbf8ceb4bbaebcf2248c67b5ab2f435e7020ea967e4fae454538bc230fd0e3f`) |
| `WebhookKit.Testing` | `1.0.0` | `WebhookKit.Testing.1.0.0.nupkg` (`87431` bytes, SHA-256 `008302d6dfa3b1559a0c3c5e44ca1cbee824ee52ed708820794e364499542d5c`) | `WebhookKit.Testing.1.0.0.snupkg` (`51548` bytes, SHA-256 `0848e30dbf35c9909adbc93bab70a0f0f4fa5baf5ca3ac6473fd206b66c7b1ff`) |

The hashes were recorded during the controlled `-KeepArtifacts` run. The default validator was run again afterward and `artifacts/packages` was confirmed absent.

## Quality gate

The exact commands from the Task 36 brief were run:

```powershell
dotnet restore "Ehsan.Webhook.Kit.slnx"
dotnet build "Ehsan.Webhook.Kit.slnx" -c Release
dotnet test "Ehsan.Webhook.Kit.slnx" -c Release --no-build
powershell -ExecutionPolicy Bypass -File "scripts\validate-packages.ps1" -Configuration Release
```

`WEBHOOKKIT_REDIS_CONNECTION=127.0.0.1:46583` was set only in the child process for the test and package commands. It was not written to a repository file or persisted as a process-wide user environment variable.

| Gate | Result | Evidence |
| --- | --- | --- |
| Restore | Pass | All solution projects restored/up to date; no restore errors |
| Release build | Pass | 0 compiler warnings, 0 analyzer warnings, 0 errors |
| Full solution test | Pass on final rerun | 502 passed, 0 failed, 0 skipped; all 8 live Redis rows executed |
| Format verification | Pass | `dotnet format "Ehsan.Webhook.Kit.slnx" --verify-no-changes --no-restore --verbosity minimal` exited 0 with no changes |
| Package validation | Pass | Six nupkgs and six snupkgs inspected; no metadata, dependency, symbol, SourceLink, or forbidden-entry errors |

The first full solution test invocation had one unrelated Core timeout in `DispatchAsync_WhenPayloadAccessRacesWithCancellation_PropagatesCancellation` after 269/270 passed. That row passed in isolation, and the exact full command was rerun with the recorded 502/0/0 result. The transient failure is disclosed rather than hidden.

### Sequential per-project test counts

| Project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| `WebhookKit.Abstractions.Tests` | 21 | 0 | 0 |
| `WebhookKit.Core.Tests` | 270 | 0 | 0 |
| `WebhookKit.AspNetCore.Tests` | 133 | 0 | 0 |
| `WebhookKit.EntityFrameworkCore.Tests` | 25 | 0 | 0 |
| `WebhookKit.IntegrationTests` | 19 | 0 | 0 |
| `WebhookKit.Redis.Tests` | 34 | 0 | 0 |
| **Total** | **502** | **0** | **0** |

## Real Redis evidence

### Disposable container lifecycle

- **Immutable image:** `redis:7.4.5@sha256:90e7a336d044f1abc9e9dbc05d65566850896d11453bbd1dd0fb7e5059f0e8fb`.
- **Final container:** `webhookkit-task36-5d78f6599ac3`.
- **Final container ID:** `435ce97c6507ed9faaa048107b3e1bbe0e8d11cfc6101ee6dcb0e65c1db334d0`.
- **Published endpoint:** `127.0.0.1:46583` only; the container port was not published on a non-loopback address.
- **Final mount inspection:** `[]`; `/data` was supplied as tmpfs, so the final container had no Docker volume mount.
- **Final cleanup:** the container was removed with volumes requested, `docker ps -a` showed it absent, and `Get-NetTCPConnection` showed port 46583 closed.
- **Discarded launch disclosure:** an initial no-`-v` launch (`webhookkit-task36-20321ab16061`, port 46349) was stopped and removed with `docker rm --volumes` because the pinned image declares `/data` and Docker created an anonymous volume. That container and its volume were absent and its port was closed before the final container was started. No other container was used or modified.

### Live Redis test matrix

The permanent opt-in rows are in `tests/WebhookKit.Redis.Tests/RedisLiveStoreTests.cs`. They skip only when `WEBHOOKKIT_REDIS_CONNECTION` is absent, with a clear reason. With the variable set, all rows execute against the real `RedisWebhookStore`.

| Row | Real Redis behavior verified |
| --- | --- |
| `RealRedisStore_RoundTripsEveryFieldAndRejectsDuplicate` | Atomic create; provider/event and Webhook ID lookup; complete record round trip including raw bytes, headers, metadata, and detached output; duplicate rejection; no duplicate record; cleanup |
| `RealRedisStore_ProtectsLeaseTransitionsAndStaleOwners` | Claim conflict; owned release; reclaim; stale-owner release/processed/failed/update rejection; current-owner update; terminal processed transition; cleanup |
| `RealRedisStore_MarksFailedAndRecoversWaitingAndExpiredLeases` | Wrong-owner failure rejection; safe failed diagnostics; terminal failure; recovery of received and expired-processing records; cleanup |
| `RealRedisStore_AcceptsGeneratorCompatibleUlidCharacters` | Regression for generator-emitted `S`/`W` ULID characters; create and lookup against real Redis; cleanup |
| `RealRedisStore_ExposesIndependentDeduplicationAndRecordTtls` | Short custom 500 ms deduplication TTL and 2 s record TTL; independent expiration; bounded 25 ms polling with a 5 s bound; cleanup |
| Existing real concurrency theory, levels 10/100/1000 | One atomic create, one processing claim, one handler execution, all other deliveries duplicate; real Redis store and cleanup |

All five new rows passed, all three existing concurrency rows passed, and the Redis project finished with 34 passed, 0 failed, and 0 skipped. Unique prefixes were used for every row. All Redis keys were deleted in each test `finally` block; no arbitrary sleep was used for synchronization.

## Sample matrix

The four exact Task 29 Release build commands all completed with 0 warnings and 0 errors:

```powershell
dotnet build "samples\MinimalApi\MinimalApi.csproj" -c Release
dotnet build "samples\Mvc\Mvc.csproj" -c Release
dotnet build "samples\Redis\Redis.csproj" -c Release
dotnet build "samples\EntityFrameworkCore\EntityFrameworkCore.csproj" -c Release
```

| Sample | Build | Runtime evidence |
| --- | --- | --- |
| Minimal API | Pass, 0 warnings/errors | Task 29 live evidence: valid/duplicate/tampered `200/200/401`; not rerun because Task 36 changed no relevant release metadata or API |
| MVC | Pass, 0 warnings/errors | Task 29 live evidence: valid/duplicate/tampered `200/200/401`; not rerun because Task 36 changed no relevant release metadata or API |
| Redis | Pass, 0 warnings/errors | Task 36 live run against the disposable Redis: current signed request twice and tampered request returned `202/202/401`; process stopped, sample port closed, and sample keys cleaned |
| Entity Framework Core | Pass, 0 warnings/errors | Task 29 live evidence: valid/duplicate/tampered `200/200/401`; not rerun because Task 36 changed no relevant release metadata or API |

No committed `.env`, secret, credential, or connection file is present (`TRACKED_SECRET_ENV_FILES=0`). Samples require runtime environment values by design: Minimal API/MVC require `WEBHOOKKIT_PROVIDER_SECRET`; Redis additionally requires `WEBHOOKKIT_REDIS_CONNECTION`; EF Core additionally requires `WEBHOOKKIT_SQLITE_CONNECTION`. Builds require no secret, and the Redis live run supplied values only to child processes.

## Benchmark baseline

The reproducible baseline is [WebhookKit.Benchmarks.WebhookKitBenchmarks-report-github.md](../BenchmarkDotNet.Artifacts/results/WebhookKit.Benchmarks.WebhookKitBenchmarks-report-github.md), generated with BenchmarkDotNet 0.14.0 on Windows 11, .NET 8.0.31, and the hardware context above.

The ShortRun baseline used 3 iterations, 1 launch, and 3 warmup iterations. Means were: HMAC-SHA256 verification `3,730.5 ns`, HMAC-SHA512 verification `2,586.0 ns`, JSON deserialization `574.9 ns`, duplicate atomic create `886.4 ns`, verification-context construction `396.3 ns`, webhook-context construction `665.8 ns`, and accepted atomic create at invocation count 64 `2,856.8 ns`.

This is a wide ShortRun confidence caveat, not a statistically strong performance claim. A longer/wider benchmark campaign remains future work.

## Security, API, CI, and package status

- **Security audit:** `Docs/security-audit.md` records the internal source/test audit dated 2026-09-24. Task 36 closes its previously explicit real-Redis runtime evidence gap for the covered store paths; it does not claim an external penetration test, certification, or production infrastructure assessment.
- **Public API review:** ADR 0013 is accepted for the six-assembly `1.0.0` surface. The candidate correction is internal validation only; no public API or package dependency surface changed.
- **CI:** `.github/workflows/ci.yml` and `.github/workflows/release.yml` contain pinned actions, restore/build/test/format/package gates, the same Redis image digest, protected release environment wiring, and OIDC publication steps. GitHub-hosted CI was not run in this local release gate.
- **Package validator:** `scripts/validate-packages.ps1 -Configuration Release` passed twice; the retained-artifact run recorded the hashes above, and default cleanup was verified.

## Known limitations and release boundaries

1. The in-process queue is bounded and non-durable across process restarts; persisted storage remains the recovery authority.
2. Redis and EF persistence can retain raw bodies and captured headers, including sensitive values. Encryption, access control, retention, backup protection, and redaction remain deployment responsibilities.
3. BenchmarkDotNet ShortRun results are directional and do not provide wide-run confidence.
4. External GitHub CI has not yet been run against this candidate.
5. The protected release environment, `NUGET_USER`, and nuget.org publication policy are not configured locally.
6. No publication, tag creation, or push was performed.

## Final external handoff state

### Authorization boundary

User authorization for this work is limited to local implementation and local commits. It explicitly excludes creating or pushing a tag, creating a remote branch, pushing any remote ref, creating a GitHub release, publishing to NuGet or nuget.org, and configuring or changing any external release system. This handoff grants no authority to perform any of those actions.

### Candidate package handoff

The six package IDs and versions are fixed as follows. The candidate artifacts were generated under `artifacts/packages` during the controlled retained-artifact validation run, then removed by the subsequent default validator cleanup; `artifacts/packages` is currently absent. The listed names and paths identify the locally validated candidate set and its exact regeneration locations, not retained files or published packages.

| Package ID | Version | Library candidate path | Symbol candidate path |
| --- | --- | --- | --- |
| `WebhookKit.Abstractions` | `1.0.0` | `artifacts/packages/WebhookKit.Abstractions.1.0.0.nupkg` | `artifacts/packages/WebhookKit.Abstractions.1.0.0.snupkg` |
| `WebhookKit.Core` | `1.0.0` | `artifacts/packages/WebhookKit.Core.1.0.0.nupkg` | `artifacts/packages/WebhookKit.Core.1.0.0.snupkg` |
| `WebhookKit.AspNetCore` | `1.0.0` | `artifacts/packages/WebhookKit.AspNetCore.1.0.0.nupkg` | `artifacts/packages/WebhookKit.AspNetCore.1.0.0.snupkg` |
| `WebhookKit.EntityFrameworkCore` | `1.0.0` | `artifacts/packages/WebhookKit.EntityFrameworkCore.1.0.0.nupkg` | `artifacts/packages/WebhookKit.EntityFrameworkCore.1.0.0.snupkg` |
| `WebhookKit.Redis` | `1.0.0` | `artifacts/packages/WebhookKit.Redis.1.0.0.nupkg` | `artifacts/packages/WebhookKit.Redis.1.0.0.snupkg` |
| `WebhookKit.Testing` | `1.0.0` | `artifacts/packages/WebhookKit.Testing.1.0.0.nupkg` | `artifacts/packages/WebhookKit.Testing.1.0.0.snupkg` |

The exact candidate filenames, byte sizes, and SHA-256 values are preserved in the version table above. To regenerate and inspect these six `.nupkg` and six `.snupkg` files at the paths above, use:

```powershell
powershell -ExecutionPolicy Bypass -File "scripts\validate-packages.ps1" -Configuration Release -KeepArtifacts
```

The normal validation command without `-KeepArtifacts` removes the controlled output afterward.

### Committed validation evidence

Task 36's exact committed quality-gate commands were:

```powershell
dotnet restore "Ehsan.Webhook.Kit.slnx"
dotnet build "Ehsan.Webhook.Kit.slnx" -c Release
dotnet test "Ehsan.Webhook.Kit.slnx" -c Release --no-build
powershell -ExecutionPolicy Bypass -File "scripts\validate-packages.ps1" -Configuration Release
```

The exact format command was:

```powershell
dotnet format "Ehsan.Webhook.Kit.slnx" --verify-no-changes --no-restore --verbosity minimal
```

The four exact sample Release build commands were:

```powershell
dotnet build "samples\MinimalApi\MinimalApi.csproj" -c Release
dotnet build "samples\Mvc\Mvc.csproj" -c Release
dotnet build "samples\Redis\Redis.csproj" -c Release
dotnet build "samples\EntityFrameworkCore\EntityFrameworkCore.csproj" -c Release
```

For the committed live-Redis gate, `WEBHOOKKIT_REDIS_CONNECTION=127.0.0.1:46583` was supplied only to the child test and package-validation processes. The final committed full-solution result was **502 passed, 0 failed, 0 skipped**, with all eight live Redis cases executed. The one earlier transient Core timeout and its successful focused/full reruns remain disclosed above.

Task 36's fresh deterministic verification explicitly removed the opt-in Redis variable and used the same build/test commands:

```powershell
$env:WEBHOOKKIT_REDIS_CONNECTION = $null
dotnet test "Ehsan.Webhook.Kit.slnx" -c Release --no-build
```

That fresh result was **494 passed, 0 failed, 6 skipped**: exactly the five opt-in `RedisLiveStoreTests` rows and the three-case live Redis concurrency theory, with no deterministic test skipped. Fresh restore/build/format/package validation also passed, and all four sample builds completed with zero warnings and zero errors. Task 37 did not rerun these commands; it records the committed Task 36 evidence as directed.

### Protected OIDC workflow handoff

The committed OIDC publication workflow is `.github/workflows/release.yml`. Its publish job is gated on a protected `v*` tag and the GitHub Environment named `release`. The future repository-owner and nuget.org trusted-publishing values are exact and must remain subject to separate owner configuration and authorization:

| Setting | Required value |
| --- | --- |
| Trusted-publishing repository owner | `0ehsan-sh0` |
| Trusted-publishing repository | `Ehsan.Webhook.Kit` |
| Trusted-publishing workflow | `release.yml` |
| GitHub Environment | `release` |
| Environment secret used by the workflow | `NUGET_USER`, containing the nuget.org username rather than an API key |

The repository owner must separately configure the protected release-tag rules so the intended `v*` tag makes `github.ref_protected` true, configure required reviewers for the `release` Environment, add the `NUGET_USER` Environment secret, and create the nuget.org trusted-publishing policy with the exact owner, repository, and workflow values above. These external settings are not locally configured, were not queried or changed for this handoff, and are not claimed to exist. The workflow receives only the short-lived OIDC login output; no credential or API key is present in this checklist.

### Local status, external gates, and handoff checklist

- [x] The candidate is a local-only, unpublished handoff; no tag, GitHub release, NuGet publication, remote branch, push, git-config change, environment change, secret creation, or external release configuration occurred.
- [x] The six package IDs, versions, candidate filenames/paths, and cleaned artifact state are explicit.
- [x] The exact validation, test, build, and format commands are recorded with committed 502/0/0 live-Redis and fresh 494/0/6 deterministic results.
- [x] The protected OIDC workflow filename and exact future owner configuration values are recorded without claiming the external settings exist.
- [x] Existing evidence, caveats, transient failure disclosure, benchmark limitations, storage/security limitations, and sample evidence are preserved.
- [ ] The external owner must review this handoff, run GitHub CI for the candidate, and confirm or configure the protected `release` Environment, `NUGET_USER`, protected release-tag rules, and nuget.org trusted-publishing policy.
- [ ] NuGet publication requires a separate explicit authorization after every external gate is confirmed; this checklist does not instruct or authorize publication now.

**Next authorized action:** repository-owner/reviewer handoff review of this local candidate and its external gate requirements. Stop at that review unless a later, explicit authorization separately authorizes an external release action.

## Release conclusion

The v1.0.0 candidate is locally validated and ready for final comprehensive review/external handoff, not published. The remaining work is external review and separately authorized release preparation; this checklist makes no publication or tag claim.
