using WebhookKit.Abstractions;

namespace WebhookKit.EntityFrameworkCore;

/// <summary>Relational persistence model for one webhook delivery.</summary>
/// <remarks>The entity is mutable for EF Core change tracking; callers should normally use <see cref="EfCoreWebhookStore{TContext}"/> instead.</remarks>
public sealed class WebhookEntity
{
    /// <summary>WebhookKit transmission identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Application correlation identifier, when available.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Configured provider name.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Provider event identifier, when available.</summary>
    public string? EventId { get; set; }

    /// <summary>Provider-scoped deduplication key.</summary>
    public string DeduplicationKey { get; set; } = string.Empty;

    /// <summary>Provider event type, when available.</summary>
    public string? EventType { get; set; }

    /// <summary>Ingress HTTP method.</summary>
    public string HttpMethod { get; set; } = string.Empty;

    /// <summary>Ingress request path.</summary>
    public string RequestPath { get; set; } = string.Empty;

    /// <summary>Serialized request headers.</summary>
    public string HeadersJson { get; set; } = "{}";

    /// <summary>Request content type, when present.</summary>
    public string? ContentType { get; set; }

    /// <summary>Request content length, when present.</summary>
    public long? ContentLength { get; set; }

    /// <summary>Exact persisted request bytes, when raw-body persistence is enabled.</summary>
    public byte[]? RawBody { get; set; }

    /// <summary>UTC instant at which the request was received.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>UTC tick representation used for provider-neutral recovery ordering.</summary>
    public long ReceivedAtTicks { get; set; }

    /// <summary>Provider event timestamp, when extracted.</summary>
    public DateTimeOffset? ProviderTimestamp { get; set; }

    /// <summary>Current processing lifecycle state.</summary>
    public WebhookProcessingStatus Status { get; set; }

    /// <summary>Number of processing attempts.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Instant the most recent attempt started.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Current processing lease owner, when one is held.</summary>
    public string? ProcessingLeaseOwner { get; set; }

    /// <summary>Current processing lease expiry.</summary>
    public DateTimeOffset? ProcessingLeaseExpiresAt { get; set; }

    /// <summary>UTC tick representation of the lease expiry for indexed recovery queries.</summary>
    public long? ProcessingLeaseExpiresAtTicks { get; set; }

    /// <summary>Successful completion time.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Failure time.</summary>
    public DateTimeOffset? FailedAt { get; set; }

    /// <summary>Safe diagnostic failure reason.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Stable machine-readable failure code.</summary>
    public string? FailureCode { get; set; }

    /// <summary>Concurrency version maintained by the store.</summary>
    public long Version { get; set; }
}
