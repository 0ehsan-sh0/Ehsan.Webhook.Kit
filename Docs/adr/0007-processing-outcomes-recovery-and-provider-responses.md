# 0007: Processing Outcomes, Recovery, and Provider Responses

Matching handlers execute sequentially and fail fast; a failure in any handler makes the delivery unsuccessful and triggers the configured retry policy. Unknown event types are acknowledged as successful provider deliveries but retained as `Ignored`. The store, not the in-process channel, is the recovery source of truth: workers reclaim waiting or lease-expired records, while HTTP responses expose only a stable safe problem shape and never raw bodies, signatures, secrets, or exception details.

## Considered Options

- Run all matching handlers concurrently: rejected because it makes failure ordering and scoped side effects difficult to reason about.
- Return HTTP 200 for unknown events without recording them: rejected because operational history would be lost.
- Treat the channel as the durable queue: rejected because process restarts can discard it.
- Expose exception details in provider responses: rejected because they can disclose secrets and internal implementation details.

## Consequences

Unknown events are operationally visible but do not execute business logic. A retry exhausted delivery is marked `Failed`; async workers can recover persisted work after restart, while the channel remains an optimization rather than the source of truth. Response status codes remain configurable with the documented defaults.
