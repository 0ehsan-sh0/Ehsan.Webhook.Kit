# MVC sample

This sample is a real WebhookKit consumer using controllers, the shared ASP.NET Core registration, and synchronous processing. The controller action uses the delivered `[WebhookEndpoint]` attribute and does not implement signature verification or deduplication itself.

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

The route attribute selects the configured provider. The provider header is included to make the request contract explicit; signature verification uses the configured secret and body, not the provider header.

## Configure

Run these commands from the repository root in PowerShell. Replace the placeholder locally. Do not create or commit a `.env` file.

```powershell
$env:WEBHOOKKIT_PROVIDER_SECRET = "<replace-me>"
```

The application reads `WEBHOOKKIT_PROVIDER_SECRET` during startup and exits with a safe configuration error if it is missing or blank. The secret is not written to logs or responses.

## Run

```powershell
dotnet run --project "samples\Mvc\Mvc.csproj" --configuration Release --no-launch-profile --urls http://localhost:50562
```

Open `requests.http`, generate current values, replace `@timestamp` and `@signature`, and send the requests. Generate values immediately before sending because the configured replay window is five minutes.

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

Copy the two output values into the request file. The valid signature covers only the body bytes; do not add a newline or sign the headers.

## MVC behavior

`Program.cs` calls `AddControllers`, `AddWebhookKit`, `AddWebhookKitAspNetCore`, and `AddWebhookHandler`. `Controllers/WebhooksController.cs` declares the actual `[WebhookEndpoint("sample-provider")]` action. The action calls `WebhookContext.GetPayload<OrderCreated>()` and returns `Ok()`; the shared endpoint filter has already completed verification, deduplication, and the response status, so the action does not write a second response.

## Expected responses

- The first correctly signed request returns `200 OK` with an empty acknowledgement and runs the typed handler once.
- The same event ID and body returns `200 OK`; the endpoint filter short-circuits the action and the handler does not run again.
- A tampered signature returns `401 Unauthorized` with a safe problem response and no action or handler execution.
- The intentionally expired timestamp example returns `400 Bad Request` with a safe problem response.

The handler logs only the provider, event ID, and event type. It does not log the secret, body, signature, or request headers. Stop the process with `Ctrl+C` when finished.
