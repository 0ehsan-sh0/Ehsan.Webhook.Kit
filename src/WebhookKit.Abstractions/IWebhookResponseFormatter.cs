namespace WebhookKit.Abstractions;

/// <summary>Stable outcomes used to select a safe provider response.</summary>
public enum WebhookEndpointOutcome
{
    /// <summary>The delivery was handled successfully in the synchronous path.</summary>
    Processed = 0,
    /// <summary>The delivery was durably admitted for asynchronous processing.</summary>
    Accepted = 1,
    /// <summary>The provider event was already recorded.</summary>
    Duplicate = 2,
    /// <summary>The verified event had no registered handler.</summary>
    Ignored = 3,
    /// <summary>Signature authentication failed.</summary>
    InvalidSignature = 4,
    /// <summary>Timestamp or replay validation failed.</summary>
    InvalidTimestamp = 5,
    /// <summary>The provider event identifier was required but unavailable.</summary>
    MissingEventId = 6,
    /// <summary>The provider event type was required but unavailable.</summary>
    MissingEventType = 7,
    /// <summary>The verified payload could not be processed.</summary>
    PayloadInvalid = 8,
    /// <summary>The request body exceeded the effective size limit.</summary>
    PayloadTooLarge = 9,
    /// <summary>The asynchronous queue could not accept the delivery.</summary>
    QueueUnavailable = 10,
    /// <summary>Application processing failed after the configured retry policy.</summary>
    ProcessingFailed = 11,
    /// <summary>The endpoint or provider configuration is invalid.</summary>
    ConfigurationError = 12,
    /// <summary>Compatibility alias for <see cref="InvalidSignature"/>.</summary>
    SignatureVerificationFailed = InvalidSignature,
    /// <summary>Compatibility alias for <see cref="InvalidTimestamp"/>.</summary>
    ReplayFailed = InvalidTimestamp,
    /// <summary>Compatibility alias for <see cref="InvalidTimestamp"/>.</summary>
    MissingTimestamp = InvalidTimestamp,
    /// <summary>Compatibility alias for <see cref="PayloadInvalid"/>.</summary>
    PayloadFailure = PayloadInvalid,
    /// <summary>Compatibility alias for <see cref="QueueUnavailable"/>.</summary>
    QueueFull = QueueUnavailable,
    /// <summary>Compatibility alias for <see cref="ProcessingFailed"/>.</summary>
    ProcessingFailure = ProcessingFailed
}

/// <summary>Safe external problem details for a rejected or failed webhook.</summary>
/// <remarks>Messages and codes must not contain raw bodies, signatures, secrets, or parser details.</remarks>
public sealed record WebhookResponseProblem
{
    /// <summary>Creates an empty problem record for object initializers.</summary>
    public WebhookResponseProblem()
    {
    }

    /// <summary>Creates a safe problem record.</summary>
    /// <param name="code">A stable machine-readable code.</param>
    /// <param name="message">A safe human-readable message.</param>
    /// <param name="traceId">An optional trace identifier.</param>
    public WebhookResponseProblem(string code, string message, string? traceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
        TraceId = string.IsNullOrWhiteSpace(traceId) ? null : traceId;
    }

    /// <summary>Stable machine-readable response code.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Safe human-readable response message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Optional trace identifier for support correlation.</summary>
    public string? TraceId { get; init; }
}

/// <summary>Maps endpoint outcomes to safe response problem details.</summary>
public interface IWebhookResponseFormatter
{
    /// <summary>Formats an outcome without exposing request secrets or raw payload data.</summary>
    /// <param name="outcome">The endpoint outcome to format.</param>
    /// <param name="traceId">An optional trace identifier to include.</param>
    /// <returns>The safe problem details for the outcome.</returns>
    WebhookResponseProblem Format(WebhookEndpointOutcome outcome, string? traceId = null);
}
