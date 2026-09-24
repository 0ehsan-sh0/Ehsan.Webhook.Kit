# 0009: Provider Customization and Payload Validation

Provider configuration supports common header and JSON extraction paths, while DI registration allows replacement or extension by custom extractors without adding provider-specific packages. A verified delivery that cannot produce required typed payload metadata is a non-retryable payload error; it is not passed to a handler as a partial object. This keeps provider flexibility separate from the security and correctness guarantees of the core pipeline.

## Considered Options

- Support only built-in Stripe/GitHub extractors: rejected because WebhookKit is intended to remain provider-neutral and vendor adapters are deferred.
- Allow handlers to receive partially deserialized payloads: rejected because partial objects can make invalid data look valid to business logic.
- Retry malformed JSON: rejected because the same invalid bytes will not become valid on a later attempt.

## Consequences

Custom providers can be integrated through DI and options without changing the core package. Payload failures are observable as `Failed` records and mapped to a safe client-error response, with no parser or secret details exposed to the provider.
