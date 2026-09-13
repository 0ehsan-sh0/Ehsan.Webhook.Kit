# 0003: Foundations Configuration and Clock (options shapes, validation, clock placement)

Tasks 03-04 define `WebhookKitOptions{long MaxRequestBodySizeBytes=1MB, Providers}` with case-insensitive provider registry (duplicate/empty → `WebhookConfigurationException`), option shapes only for signatures (`HeaderName, Algorithm HmacSha256/512, Secret + SecretsForRotation, Encoding Hex/Base64`), timestamps (`HeaderName, Tolerance=5min, AllowMissing=false`), and `EventId/HeaderName` strings — engines deferred to Subgroup 02 — validated at startup via `IValidateOptions<WebhookKitOptions>` without logging secrets; and place minimal `IWebhookClock{DateTimeOffset UtcNow}` in `Abstractions` with singleton `SystemWebhookClock` in `Core` plus locked mutable `FakeWebhookClock{Advance()}` transient in `Testing` instead of using `TimeProvider` directly.

## Considered Options
- `int` size / case-sensitive dict / runtime failures (rejected: overflow risk, `Stripe` vs `stripe` fork, late failures).
- Single `Secret string` only (rejected: rotation per §75-76 would break options later).
- `TimeProvider` directly (rejected:heavier mocking ceremony for consumers; one-line adapter remains possible).

## Consequences
- Startup fail-fast on bad config; per-provider body-size overrides and engine logic explicitly deferred.
- All timestamp-sensitive logic must depend on `IWebhookClock`; Fake never singleton in prod.
