# Redis sample

This runnable sample uses the Redis store and explicitly asynchronous endpoint mode. It demonstrates `AddWebhookKit`, `AddWebhookKitAspNetCore`, `AddWebhookKitRedis`, `AddWebhookHandler`, `MapWebhook`, and the delivered hosted worker.

## Contract

- Provider: `sample-provider`
- Event Type: `order.created`
- Endpoint: `POST /webhooks/sample`
- Payload: `{"orderId":"ord_1001","customerId":"cus_2001","amount":1250}`
- `X-Webhook-Provider`: `sample-provider`
- `X-Webhook-Event-Id`: a provider Event ID such as `evt-order-1001`
- `X-Webhook-Event-Type`: `order.created`
- `X-Webhook-Timestamp`: current Unix timestamp in seconds
- `X-Webhook-Signature`: lowercase hexadecimal HMAC-SHA256 of the exact request body

The route selects `sample-provider`. The provider header is included to make the request contract explicit; the signature is computed from the configured secret and body, not from the provider header. The provider must send the configured Event ID, Event Type, timestamp, and signature headers.

See the [root guide](../../README.md), [configuration reference](../../Docs/configuration.md), and [verification notes](../../Docs/verification.md) for the delivered API contract and the Redis evidence boundary.

## Configure Redis and the secret

Run from the repository root in PowerShell. Replace the secret placeholder locally. Do not create or commit a `.env` file or put Redis credentials in source control.

```powershell
$env:WEBHOOKKIT_PROVIDER_SECRET = "<replace-me>"
$env:WEBHOOKKIT_REDIS_CONNECTION = "localhost:6379"
```

`WEBHOOKKIT_REDIS_CONNECTION` is a StackExchange.Redis connection string. A password-protected service can use a value shaped like `localhost:6379,password=<redis-password>,ssl=true`; never commit the password. A startup-only wiring check can use `localhost:6379,abortConnect=true`, but that setting does not make a live request valid.

The application reads both variables during startup and exits with a safe configuration error if either is missing or blank. It does not log the secret or connection string, and WebhookKit responses remain safe.

## Build and run

Start a real Redis service separately, then run:

```powershell
dotnet build "samples\Redis\Redis.csproj" -c Release
dotnet run --project "samples\Redis\Redis.csproj" --configuration Release --no-launch-profile --urls http://localhost:50563
```

Do not substitute the in-memory store for this sample. Redis is needed for live deduplication and record operations.

## Generate a current timestamp and signature

This block uses the same body as the request file and prints only the timestamp and signature:

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

Copy the two output values into `requests.http`. The valid signature covers only the exact body bytes; do not add a newline or sign the headers. Generate values immediately before sending because the configured replay window is five minutes.

## Expected responses

- The first correctly signed request returns `202 Accepted` after verification, Redis persistence, and queue admission. The worker handles it asynchronously.
- The same Event ID and body returns `202 Accepted`; Redis deduplication prevents a second handler execution.
- A tampered signature returns `401 Unauthorized` with a safe problem response and no Redis record or handler execution.
- The intentionally expired timestamp example returns `400 Bad Request` with a safe problem response.

An asynchronous acknowledgement means the delivery was accepted, not that business processing has completed. The delivered queue is a bounded in-process channel and is not durable across process restarts. Redis persistence is the store authority and can make records recoverable, but it does not make the channel durable. The handler logs only provider, Event ID, and Event Type; it does not log the secret, body, signature, or request headers.

**Redis evidence boundary:** the committed standalone local gate ran this sample successfully against a disposable Redis 7.4.5 service and recorded `202/202/401` for valid, duplicate, and tampered requests. That gate does not replace validation in your production topology. Redis Cluster, managed-service identity/TLS/network policy, failover, capacity, backup/monitoring, and deployment controls remain deployment-specific gates. Start your own Redis service for this sample and stop the process with `Ctrl+C` when finished.
