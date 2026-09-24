using WebhookKit.Abstractions;

namespace WebhookKit.EntityFrameworkCore;

public sealed class WebhookEntity
{
    public string Id { get; set; } = string.Empty;

    public string? CorrelationId { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string? EventId { get; set; }

    public string DeduplicationKey { get; set; } = string.Empty;

    public string? EventType { get; set; }

    public string HttpMethod { get; set; } = string.Empty;

    public string RequestPath { get; set; } = string.Empty;

    public string HeadersJson { get; set; } = "{}";

    public string? ContentType { get; set; }

    public long? ContentLength { get; set; }

    public byte[]? RawBody { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public long ReceivedAtTicks { get; set; }

    public DateTimeOffset? ProviderTimestamp { get; set; }

    public WebhookProcessingStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public string? ProcessingLeaseOwner { get; set; }

    public DateTimeOffset? ProcessingLeaseExpiresAt { get; set; }

    public long? ProcessingLeaseExpiresAtTicks { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }

    public string? FailureReason { get; set; }

    public string? FailureCode { get; set; }

    public long Version { get; set; }
}
