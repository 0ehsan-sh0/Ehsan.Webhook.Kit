# 0006: Deduplication Claims and Event-ID Fallback

The store owns an atomic deduplication claim, not an exists-then-create check. The default policy requires a provider Event ID; providers without an Event ID may explicitly opt into a deterministic body-hash fallback. The store contract will support the claim and lease operations needed by background processing while preserving the existing store abstraction across in-memory, Redis, and EF Core providers.

## Considered Options

- Always use a timestamp/body hash when Event ID is missing: rejected because timestamps can differ across provider retries and weaken duplicate identity.
- Disable deduplication when Event ID is missing: rejected because it permits repeated handler execution by default.
- Keep the original read-only create/update-only store contract: rejected because an async worker cannot safely recover or claim pending work without races.

## Consequences

The deduplication key has a documented provider-scoped identity policy, and background workers can coordinate claims without duplicate concurrent processing. Provider adapters can choose a deterministic fallback explicitly, while the default remains fail-fast for missing Event IDs.
