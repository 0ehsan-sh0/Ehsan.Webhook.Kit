using Microsoft.Extensions.Logging;

namespace WebhookKit.Core.Diagnostics;

internal static partial class WebhookLogMessages
{
    [LoggerMessage(EventId = 1801, Level = LogLevel.Information, Message = "Webhook received. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId}")]
    public static partial void Received(
        ILogger logger,
        string webhookId,
        string provider,
        string? eventId,
        string? eventType,
        string status,
        int attempt,
        string? traceId,
        string? correlationId);

    [LoggerMessage(EventId = 1802, Level = LogLevel.Information, Message = "Webhook verified. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId}")]
    public static partial void Verified(
        ILogger logger,
        string webhookId,
        string provider,
        string? eventId,
        string? eventType,
        string status,
        int attempt,
        string? traceId,
        string? correlationId);

    [LoggerMessage(EventId = 1803, Level = LogLevel.Warning, Message = "Webhook rejected. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId} FailureCode={FailureCode}")]
    public static partial void Rejected(
        ILogger logger,
        string webhookId,
        string provider,
        string? eventId,
        string? eventType,
        string status,
        int attempt,
        string? traceId,
        string? correlationId,
        string failureCode);

    [LoggerMessage(EventId = 1804, Level = LogLevel.Information, Message = "Webhook duplicate. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId}")]
    public static partial void Duplicate(
        ILogger logger,
        string webhookId,
        string provider,
        string? eventId,
        string? eventType,
        string status,
        int attempt,
        string? traceId,
        string? correlationId);

    [LoggerMessage(EventId = 1805, Level = LogLevel.Information, Message = "Webhook ignored. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId}")]
    public static partial void Ignored(
        ILogger logger,
        string webhookId,
        string provider,
        string? eventId,
        string? eventType,
        string status,
        int attempt,
        string? traceId,
        string? correlationId);

    [LoggerMessage(EventId = 1806, Level = LogLevel.Debug, Message = "Webhook processing. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId}")]
    public static partial void Processing(
        ILogger logger,
        string webhookId,
        string provider,
        string? eventId,
        string? eventType,
        string status,
        int attempt,
        string? traceId,
        string? correlationId);

    [LoggerMessage(EventId = 1807, Level = LogLevel.Information, Message = "Webhook processed. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId}")]
    public static partial void Processed(
        ILogger logger,
        string webhookId,
        string provider,
        string? eventId,
        string? eventType,
        string status,
        int attempt,
        string? traceId,
        string? correlationId);

    [LoggerMessage(EventId = 1808, Level = LogLevel.Error, Message = "Webhook failed. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId} FailureCode={FailureCode}")]
    public static partial void Failed(
        ILogger logger,
        string webhookId,
        string provider,
        string? eventId,
        string? eventType,
        string status,
        int attempt,
        string? traceId,
        string? correlationId,
        string failureCode);

    [LoggerMessage(EventId = 1809, Level = LogLevel.Warning, Message = "Webhook retry. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId} FailureCode={FailureCode}")]
    public static partial void Retry(
        ILogger logger,
        string webhookId,
        string provider,
        string? eventId,
        string? eventType,
        string status,
        int attempt,
        string? traceId,
        string? correlationId,
        string? failureCode);
}
