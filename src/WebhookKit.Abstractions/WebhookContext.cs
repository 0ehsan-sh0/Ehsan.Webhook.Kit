// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Foundations slice of the execution context passed to application handlers.
/// Shields secrets and raw buffers; exposes extracted metadata only.
/// Extended with payload helpers in Task 12/13.
/// </summary>
public sealed class WebhookContext
{
    /// <summary>WebhookKit-generated transmission identifier.</summary>
    public required string WebhookId { get; init; }

    /// <summary>Configured provider name.</summary>
    public required string Provider { get; init; }

    /// <summary>Provider event identifier, if extracted.</summary>
    public string? EventId { get; init; }

    /// <summary>Provider event type, if extracted.</summary>
    public string? EventType { get; init; }

    /// <summary>Instant the request was received.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Provider-supplied event timestamp, if extracted.</summary>
    public DateTimeOffset? ProviderTimestamp { get; init; }

    /// <summary>Request headers. Multi-value per key.</summary>
    public required IReadOnlyDictionary<string, string[]> Headers { get; init; }
}
