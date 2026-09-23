# 0010: Configurable Signature Inputs and Replay Policy

HMAC verification defaults to signing the exact raw body, but each provider may configure a signing input that combines the provider timestamp and raw bytes for providers that require timestamp-prefixed signatures. Timestamp replay validation is required by default; a provider without a usable timestamp must explicitly configure an alternative replay signal or opt out with a documented security tradeoff. Missing event type is distinct from an unregistered event type: missing type is a non-retryable payload error, while an unregistered type is acknowledged and retained as `Ignored`.

## Considered Options

- Keep raw-body-only signing for v1.0: rejected because several common provider formats include timestamps in the signed input.
- Skip timestamp validation whenever the header is missing: rejected because it silently weakens replay protection.
- Treat missing and unknown event types identically: rejected because missing routing metadata is malformed delivery, while an unknown type is a valid but unhandled event.

## Consequences

The signature verifier receives a provider-configurable signing input without exposing secrets. Provider configuration must make replay policy explicit, and consumers receive safe, stable client errors for missing event types rather than partial handler execution.
