namespace WebhookKit.Abstractions;

public enum WebhookEndpointOutcome
{
    Processed = 0,
    Accepted = 1,
    Duplicate = 2,
    Ignored = 3,
    InvalidSignature = 4,
    InvalidTimestamp = 5,
    MissingEventId = 6,
    MissingEventType = 7,
    PayloadInvalid = 8,
    PayloadTooLarge = 9,
    QueueUnavailable = 10,
    ProcessingFailed = 11,
    ConfigurationError = 12,
    SignatureVerificationFailed = InvalidSignature,
    ReplayFailed = InvalidTimestamp,
    MissingTimestamp = InvalidTimestamp,
    PayloadFailure = PayloadInvalid,
    QueueFull = QueueUnavailable,
    ProcessingFailure = ProcessingFailed
}

public sealed record WebhookResponseProblem
{
    public WebhookResponseProblem()
    {
    }

    public WebhookResponseProblem(string code, string message, string? traceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
        TraceId = string.IsNullOrWhiteSpace(traceId) ? null : traceId;
    }

    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string? TraceId { get; init; }
}

public interface IWebhookResponseFormatter
{
    WebhookResponseProblem Format(WebhookEndpointOutcome outcome, string? traceId = null);
}
