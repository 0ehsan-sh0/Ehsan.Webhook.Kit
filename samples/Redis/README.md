# Redis sample

This sample is a real WebhookKit consumer using the Redis store and asynchronous endpoint mode. It demonstrates `AddWebhookKit`, `AddWebhookKitAspNetCore`, `AddWebhookKitRedis`, `AddWebhookHandler`, and `MapWebhook` with the delivered background worker.

## Contract

All samples use the same provider and header contract:

- Provider: `sample-provider`
- Event type: `order.created`
- Endpoint: `POST /webhooks/sample`
- Payload: `{"orderId":"ord_1001","customerId":"cus_2001","amount":1250}`
- `X-Webhook-Provider`: `sample-provider`
- `X-Webhook-Event-Id`: a provider event ID such as `evt-order-1001`
- `X-Webhook-Event-Type`: `order.created`
- `X-Webhook-Timestamp`: the current Unix timestamp in seconds
- `X-Webhook-Signature`: lowercase hexadecimal HMAC-SHA256 of the exact request body

The route selects the configured provider. The provider header is included to make the request contract explicit; signature verification uses the configured secret and body, not the provider header.

## Configure Redis and the secret

Run these commands from the repository root in PowerShell. Replace the secret placeholder locally. Do not create or commit a `.env` file or put Redis credentials in source control.

```powershell
$env:WEBHOOKKIT_PROVIDER_SECRET = "<replace-me>"
$env:WEBHOOKKIT_REDIS_CONNECTION = "localhost:6379"
```

`WEBHOOKKIT_REDIS_CONNECTION` is a StackExchange.Redis connection string. A password-protected service can use a value shaped like `localhost:6379,password=<redis-password>,ssl=true`; never commit the password. A startup-only wiring check without a reachable service can use `localhost:6379,abortConnect=true`, but that setting does not make a live request valid.

The application reads both variables during startup and exits with a safe configuration error if either is missing or blank. The sample does not log the secret or connection string, and WebhookKit responses remain safe.

## Run

Start a real Redis service separately, then run:

```powershell
dotnet run --project "samples\Redis\Redis.csproj" --configuration Release --no-launch-profile --urls http://localhost:50563
```

Do not use the in-memory store as a substitute for this sample. The Redis store is needed for the live deduplication and record operations.

## Generate a current timestamp and signature

This PowerShell block uses the same exact body as the request template and prints only the timestamp and signature:

```powershell
$body = '{"orderId":"ord_1001","customerId":"cus_2001","amount":1250}'
$timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$key = [Text.Encoding]::UTF8.GetBytes($env:WEBHOOKKIT_PROVIDER_SECRET)
$hmac = [Security.Cryptography.HMACSHA256]::new($key)
try {
    $hash = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($body))
    $signature = (($hash | ForEach-Object { $_.ToString('x2') }) -join '')
}
finally {
    $hmac.Dispose()
}
"timestamp=$timestamp"
"signature=$signature"
```

Copy the two output values into the request file. The valid signature covers only the body bytes; do not add a newline or sign the headers. Generate values immediately before sending because the configured replay window is five minutes.

## Expected responses

- The first correctly signed request returns `202 Accepted` after verification, Redis persistence, and queue admission. The worker handles it asynchronously.
- The same event ID and body returns `202 Accepted`; Redis deduplication prevents a second handler execution.
- A tampered signature returns `401 Unauthorized` with a safe problem response and no Redis record or handler execution.
- The intentionally expired timestamp example returns `400 Bad Request` with a safe problem response.

An asynchronous acknowledgement means the delivery was accepted, not that business processing has already completed. The queue used by this sample is the delivered bounded in-process channel. It is not durable across process restarts, and a real Redis service is still required for the Redis-backed store and live request verification. The handler logs only the provider, event ID, and event type; it does not log the secret, body, signature, or request headers. Stop the process with `Ctrl+C` when finished.
