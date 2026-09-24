using Microsoft.Extensions.Logging;

namespace WebhookKit.AspNetCore.Diagnostics;

internal static partial class WebhookEndpointLogMessages
{
    [LoggerMessage(EventId = 1810, Level = LogLevel.Information, Message = "Webhook endpoint completed. Provider={Provider} WebhookId={WebhookId} EventId={EventId} EventType={EventType} Status={Status} Outcome={Outcome} Mode={Mode} TraceId={TraceId} CorrelationId={CorrelationId} FailureCode={FailureCode}")]
    public static partial void Completed(
        ILogger logger,
        string provider,
        string? webhookId,
        string? eventId,
        string? eventType,
        int status,
        string outcome,
        string mode,
        string? traceId,
        string? correlationId,
        string? failureCode);

    [LoggerMessage(EventId = 1811, Level = LogLevel.Warning, Message = "Webhook endpoint rejected. Provider={Provider} WebhookId={WebhookId} EventId={EventId} EventType={EventType} Status={Status} Outcome={Outcome} Mode={Mode} TraceId={TraceId} CorrelationId={CorrelationId} FailureCode={FailureCode}")]
    public static partial void Rejected(
        ILogger logger,
        string provider,
        string? webhookId,
        string? eventId,
        string? eventType,
        int status,
        string outcome,
        string mode,
        string? traceId,
        string? correlationId,
        string? failureCode);

    [LoggerMessage(EventId = 1812, Level = LogLevel.Error, Message = "Webhook endpoint failed. Provider={Provider} WebhookId={WebhookId} EventId={EventId} EventType={EventType} Status={Status} Outcome={Outcome} Mode={Mode} TraceId={TraceId} CorrelationId={CorrelationId} FailureCode={FailureCode}")]
    public static partial void Failed(
        ILogger logger,
        string provider,
        string? webhookId,
        string? eventId,
        string? eventType,
        int status,
        string outcome,
        string mode,
        string? traceId,
        string? correlationId,
        string? failureCode);
}
