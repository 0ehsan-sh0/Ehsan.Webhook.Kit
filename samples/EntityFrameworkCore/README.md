# Entity Framework Core sample

This runnable sample uses an application-owned SQLite `DbContext` and synchronous processing. It demonstrates `AddDbContext`, `AddWebhookKit`, `AddWebhookKitAspNetCore`, `AddWebhookKitEntityFrameworkCore`, `AddWebhookHandler`, and `MapWebhook`.

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

See the [root guide](../../README.md), [configuration reference](../../Docs/configuration.md), and [verification notes](../../Docs/verification.md) for the delivered API contract.

## Configure SQLite and the secret

Run from the repository root in PowerShell. Replace the secret placeholder locally. Do not create or commit a `.env` file or a database.

```powershell
$env:WEBHOOKKIT_PROVIDER_SECRET = "<replace-me>"
$env:WEBHOOKKIT_SQLITE_CONNECTION = "Data Source=sample-webhooks.db;Pooling=False"
```

`WEBHOOKKIT_SQLITE_CONNECTION` is passed directly to `UseSqlite`. A relative data source is resolved by the process working directory. An absolute path such as `Data Source=E:\temp\webhookkit-sample.db;Pooling=False` can be used when a stable location is needed. This sample creates the SQLite file and schema at startup with `EnsureCreated`; it intentionally ships no package migration or application migration file.

The application reads both variables during startup and exits with a safe configuration error if either is missing or blank. It does not log the secret or connection string, and WebhookKit responses remain safe.

## Build and run

```powershell
dotnet build "samples\EntityFrameworkCore\EntityFrameworkCore.csproj" -c Release
dotnet run --project "samples\EntityFrameworkCore\EntityFrameworkCore.csproj" --configuration Release --no-launch-profile --urls http://localhost:50565
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

## EF Core behavior

`Data/WebhookDbContext.cs` is owned by the sample application. Its `OnModelCreating` method calls `ApplyWebhookConfiguration`, and startup calls `Database.EnsureCreated()`. WebhookKit supplies the store registration; the application owns the context, connection string, schema creation, and migration policy.

## Expected responses

- The first correctly signed request returns `200 OK` with an empty acknowledgement and runs the typed handler once.
- The same Event ID and body returns `200 OK`; the SQLite unique deduplication key prevents a second handler execution.
- A tampered signature returns `401 Unauthorized` with a safe problem response and no record or handler execution.
- The intentionally expired timestamp example returns `400 Bad Request` with a safe problem response.

The handler logs only provider, Event ID, and Event Type. It does not log the secret, body, signature, or request headers. Stop the process with `Ctrl+C`, then remove the configured SQLite file and any `sample-webhooks.db-wal` or `sample-webhooks.db-shm` files created during the run.
