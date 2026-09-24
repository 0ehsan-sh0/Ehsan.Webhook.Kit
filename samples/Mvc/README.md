# MVC sample

This runnable sample uses WebhookKit with controllers, the shared ASP.NET Core adapter, the in-memory store, and synchronous processing. The controller action uses the delivered `[WebhookEndpoint]` attribute and does not implement signature verification or deduplication itself.

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

The route attribute selects `sample-provider`. The provider header is included to make the request contract explicit; the signature is computed from the configured secret and body, not from the provider header. The provider must send the configured Event ID, Event Type, timestamp, and signature headers.

See the [root guide](../../README.md), [configuration reference](../../Docs/configuration.md), and [verification notes](../../Docs/verification.md) for the delivered API contract.

## Configure

Run from the repository root in PowerShell. Replace the placeholder locally; do not create or commit a `.env` file.

```powershell
$env:WEBHOOKKIT_PROVIDER_SECRET = "<replace-me>"
```

The application reads `WEBHOOKKIT_PROVIDER_SECRET` during startup and exits with a safe configuration error if it is missing or blank. The secret is not logged or returned.

## Build and run

```powershell
dotnet build "samples\Mvc\Mvc.csproj" -c Release
dotnet run --project "samples\Mvc\Mvc.csproj" --configuration Release --no-launch-profile --urls http://localhost:50562
```

Open `requests.http`, generate current values, replace `@timestamp` and `@signature`, and send the requests. Generate values immediately before sending because the configured replay window is five minutes.

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

Copy the two output values into `requests.http`. The valid signature covers only the exact body bytes; do not add a newline or sign the headers.

## MVC behavior

`Program.cs` calls `AddControllers`, `AddWebhookKit`, `AddWebhookKitAspNetCore`, and `AddWebhookHandler`. `Controllers/WebhooksController.cs` declares `[WebhookEndpoint("sample-provider")]` on the action. The action calls `WebhookContext.GetPayload<OrderCreated>()` and returns `Ok()`; the shared endpoint filter has already completed verification, deduplication, and the response status, so rejected, duplicate, and ignored outcomes short-circuit the action.

## Expected responses

- The first correctly signed request returns `200 OK` with an empty acknowledgement and runs the typed handler once.
- The same Event ID and body returns `200 OK`; the MVC action is short-circuited and the handler does not run again.
- A tampered signature returns `401 Unauthorized` with a safe problem response and no action or handler execution.
- The intentionally expired timestamp example returns `400 Bad Request` with a safe problem response.

The handler logs only provider, Event ID, and Event Type. It does not log the secret, body, signature, or request headers. Stop the process with `Ctrl+C` when finished.
