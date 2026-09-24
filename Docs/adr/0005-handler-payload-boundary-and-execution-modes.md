# 0005: Handler Payload Boundary and Execution Modes

WebhookKit keeps raw request bytes inside the ingestion and processing pipeline. Application handlers receive an immutable execution context with extracted metadata, headers, and typed payload deserialization, but no direct raw-body access. Synchronous processing is the default; asynchronous processing is explicitly enabled and uses an in-process channel that is documented as non-durable across process restarts. This preserves the security boundary around raw data while keeping the common request path simple.

## Considered Options

- Expose `ReadOnlyMemory<byte>` directly to handlers: rejected because it increases accidental payload/secret exposure and complicates the handler contract.
- Make asynchronous processing the default: rejected because the planned initial queue is in-process and cannot guarantee delivery across restarts.
- Add a durable external broker in v1.0: rejected because it expands the product scope and couples the core package to a specific messaging technology.

## Consequences

`WebhookContext` remains safe for business code, and the pipeline owns raw-body verification and storage. Consumers that need direct raw bytes must provide an explicit internal or test seam rather than relying on the public handler context. Asynchronous mode is suitable for decoupling request latency, not for guaranteed delivery.
