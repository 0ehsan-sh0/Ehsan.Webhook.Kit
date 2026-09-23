# 0008: Claim-Based Storage, Retry Policy, and Provider Retention

WebhookKit uses claim-based storage with a time-bounded processing lease so synchronous and asynchronous processing share the same ownership rules. The default retry policy allows three total attempts with exponential backoff and jitter; application code can explicitly mark failures as retryable or permanent. Redis deduplication keys default to 7 days and full records to 30 days, while EF Core uses an application-owned storage entity with a provider-safe unique key.

## Considered Options

- Use a fixed three-second retry policy from the original roadmap: rejected because the existing options and operational needs favor a two-second initial delay and configurable values.
- Retry every exception: rejected because cancellation and invalid payloads must not be retried.
- Keep raw database TTL behavior in a background cleanup worker: rejected because Redis expiration is simpler and avoids unnecessary cleanup coordination.
- Persist `WebhookRecord` directly in the consuming EF Core model: rejected because persistence representation and the public transport record have different lifecycle needs.

## Consequences

Store implementations must provide atomic claim, terminal transition, and lease recovery behavior. Consumers can tune retention and retry behavior per provider, while the defaults remain safe and explicit. A full async channel returns a provider error without discarding the persisted delivery.
