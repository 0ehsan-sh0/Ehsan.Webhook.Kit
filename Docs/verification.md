# WebhookKit verification notes

This document records the verification boundary for the delivered user documentation. It distinguishes executable evidence from external-service evidence that was not available in this workspace.

## Verification commands

The documented repository commands are:

```powershell
dotnet restore "Ehsan.Webhook.Kit.slnx"
dotnet build "Ehsan.Webhook.Kit.slnx" -c Release
dotnet test "Ehsan.Webhook.Kit.slnx" -c Release
dotnet format "Ehsan.Webhook.Kit.slnx" --verify-no-changes --no-restore
```

The four sample build commands are:

```powershell
dotnet build "samples\MinimalApi\MinimalApi.csproj" -c Release
dotnet build "samples\Mvc\Mvc.csproj" -c Release
dotnet build "samples\Redis\Redis.csproj" -c Release
dotnet build "samples\EntityFrameworkCore\EntityFrameworkCore.csproj" -c Release
```

The repository has no separate markdown or lint command. Deterministic local-link and path checks are used instead, and the Release build runs the configured .NET analyzers.

## Fresh local results

The commands were run from the worktree on 2026-09-24 after the documentation rewrite:

- Solution Release build: succeeded with 0 warnings and 0 errors.
- Solution Release tests: Abstractions 16 passed, Core 268 passed, ASP.NET Core 133 passed, EF Core 25 passed, Integration 15 passed, Redis 26 passed and 1 expected real-service skip.
- Minimal API, MVC, Redis, and EF Core sample Release builds: each succeeded with 0 warnings and 0 errors.
- `dotnet format "Ehsan.Webhook.Kit.slnx" --verify-no-changes --no-restore`: exit 0 with no reported changes.
- Temporary README projects: both Release builds succeeded with 0 warnings and 0 errors.

## Documentation API cross-check

| Documented surface | Delivered evidence |
| --- | --- |
| `AddWebhookKit` | `src/WebhookKit.Core/DependencyInjection/WebhookKitServiceCollectionExtensions.cs` |
| `AddWebhookKitAspNetCore` | `src/WebhookKit.AspNetCore/DependencyInjection/WebhookKitAspNetCoreServiceCollectionExtensions.cs` |
| `AddWebhookHandler<THandler>` and type overload | `src/WebhookKit.Core/DependencyInjection/WebhookKitServiceCollectionExtensions.cs` |
| `MapWebhook` overloads | `src/WebhookKit.AspNetCore/DependencyInjection/WebhookEndpointRouteBuilderExtensions.cs` |
| `WebhookEndpointOptions` and `WebhookProcessingMode` | `src/WebhookKit.AspNetCore/Pipeline/WebhookEndpointOptions.cs` |
| `WebhookEndpointResponseOptions` | `src/WebhookKit.AspNetCore/Pipeline/WebhookEndpointOptions.cs` |
| `WebhookEndpointAttribute` and action policy | `src/WebhookKit.AspNetCore/Mvc/WebhookEndpointAttribute.cs` |
| Provider, signature, timestamp, storage, queue, background, and retry options | `src/WebhookKit.Core/Options` |
| `IWebhookHandler<T>` and `WebhookContext` | `src/WebhookKit.Abstractions` |
| HMAC and timestamp verification | `src/WebhookKit.Core/Verifiers` and `tests/WebhookKit.Core.Tests/WebhookSecurityTests.cs` |
| Header/JSON/composite extraction | `src/WebhookKit.Core/Extractors` and `tests/WebhookKit.Core.Tests/ExtractorTests.cs` |
| Body limits and raw-body boundary | `src/WebhookKit.AspNetCore/WebhookBodyReader.cs` and `src/WebhookKit.Abstractions/WebhookContext.cs` |
| In-memory claims, leases, and recovery | `src/WebhookKit.Core/Stores/InMemoryWebhookStore.cs` |
| Queue and hosted worker | `src/WebhookKit.Core/Queues/ChannelWebhookQueue.cs` and `src/WebhookKit.Core/Workers/WebhookBackgroundWorker.cs` |
| `AddWebhookKitRedis` and Redis TTL options | `src/WebhookKit.Redis/RedisServiceCollectionExtensions.cs` and `RedisWebhookStoreOptions.cs` |
| `AddWebhookKitEntityFrameworkCore` and `ApplyWebhookConfiguration` | `src/WebhookKit.EntityFrameworkCore/EfCoreServiceCollectionExtensions.cs` and `WebhookModelBuilderExtensions.cs` |
| Testing builder, generator, clock, and harness | `src/WebhookKit.Testing` |
| Package/project names | `src/*/*.csproj`, `samples/*/*.csproj`, and `Ehsan.Webhook.Kit.slnx` |

A fixed-string source check covered 48 documented public symbols and reported 0 missing symbols.

## Code-block verification

The first-run C# block in `README.md` was compiled in `C:\Users\Arshian\AppData\Local\Temp\opencode\webhookkit-task30-readme\FirstRun`, outside the repository, with a project reference to the delivered `WebhookKit.AspNetCore` project. The exact block defines the payload record, typed handler, required registration calls, `MapWebhook`, and `app.Run()`.

The Testing helper block was compiled in `C:\Users\Arshian\AppData\Local\Temp\opencode\webhookkit-task30-readme\Testing`, outside the repository, with a project reference to `WebhookKit.Testing`. Both Release builds completed with 0 warnings and 0 errors.

The MVC, Redis, and EF Core examples are source-equivalent to the already buildable Task 29 sample projects, which use the same public registrations and contracts. Their exact Release build commands are listed above and were rerun successfully. No throwaway project was added to the worktree.

## Link and path checks

The documentation link check verified 149 relative links across `README.md`, `Docs/configuration.md`, `Docs/verification.md`, the four sample README files, and the 38 roadmap HTML files. It reported 0 errors and 0 missing paths. A separate fixed-path check verified 17 documented solution/package/project/request/security paths with 0 missing paths. There is no existing markdown-link tool in this repository, so the check is deterministic and path-based.

## Sample consistency

All four sample contracts use the same provider/header shape and the same synthetic body in their request files:

- Provider `sample-provider` and Event Type `order.created`.
- `POST /webhooks/sample`.
- `X-Webhook-Provider`, `X-Webhook-Event-Id`, `X-Webhook-Event-Type`, `X-Webhook-Timestamp`, and `X-Webhook-Signature`.
- HMAC-SHA256, lowercase hex, raw-body signing, and a five-minute replay window.
- Minimal API, MVC, and EF Core acknowledge `200`; Redis asynchronous mode acknowledges `202`.
- Tampered signatures return `401`; expired timestamps return `400`.

The sample README run commands, environment variables, ports, and request-file base URLs are checked against the actual sample source and `requests.http` files. Redis requires a separately started real service; the sample does not substitute the in-memory store.

## Security and external boundaries

The security evidence is recorded in [security-audit.md](security-audit.md). The following boundaries remain explicit:

1. Real Redis runtime remains unexecuted in this workspace. The Redis test suite uses a deterministic stateful adapter for its local coverage, and the real-service test is skipped when `WEBHOOKKIT_REDIS_CONNECTION` is absent. Lua, `cjson`, `KEEPTTL`, expiration precision, keyspace behavior, and Cluster co-location require a real Redis validation run.
2. The in-process queue is bounded and non-durable across process restarts. The store is the recovery authority, not the channel.
3. Redis and EF persistence can retain raw bodies and captured headers, including sensitive values. Encryption, access control, retention, backup protection, and redaction remain deployment responsibilities.
4. The internal audit is not an external penetration test, formal assurance review, or security certification.

## Roadmap consistency

The dashboard at `Docs/tasks/index.html` records 30 complete tasks and 7 pending tasks. Task detail pages use the same status for Tasks 01–30 and Tasks 31–37. Tasks 01–09 are supported by the implemented foundations, tests, accepted ADRs, and the delivered ADR 0010 production signing/replay behavior. Tasks 10–29 are supported by their committed verification reports. Task 30 is supported by this documentation verification. Tasks 31–37 remain pending; no release-package, CI, benchmark, or external-publication completion is inferred.

## Self-review checklist

- The root guide follows the required order: overview, install, first-run, handler, configuration, extraction, MVC, modes, in-memory, Redis, EF Core, testing, responses, security, troubleshooting, building, contributing, and license.
- Every documented public symbol was searched in the delivered source or sample projects.
- The first-run code has no production secret or live payload.
- Sample request files use placeholders for current timestamps and signatures, and label rejection values as tampered or expired.
- No implementation, plan, ADR, CONTEXT, ledger, or source-behavior file was changed for this documentation task.
- The Task 30 report remains under `.superpowers/sdd` and is ignored by `.superpowers/sdd/.gitignore`.
