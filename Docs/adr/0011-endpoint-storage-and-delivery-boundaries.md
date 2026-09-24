# 0011: Endpoint, Storage, and Delivery Boundaries

The public Minimal API has a simple provider/path overload and an advanced options overload. MVC uses `[WebhookEndpoint(providerName)]` with the same ingestion service, while application actions own only business behavior. Request limits use a global default with per-provider overrides, raw bodies are retained for recoverable asynchronous deliveries but may be discarded after successful synchronous processing, and the EF Core package leaves migrations and `DbContext` ownership to the consuming application.

## Considered Options

- Expose a large endpoint configuration object to every consumer: rejected because the common path should remain one line and easy to understand.
- Run all four samples or leave them as placeholders: rejected because runnable examples are the clearest verification of the public API.
- Have WebhookKit own EF Core migrations: rejected because migration policy belongs to the consuming application.
- Always retain raw bodies indefinitely: rejected because synchronous processing does not need a durable raw-body copy after success.

## Consequences

The README can present one Minimal API path first, with MVC, Redis, EF Core, and async configuration as separate examples. Provider-specific limits and storage policies are visible and testable, while the package boundaries remain aligned with ADR 0001.
