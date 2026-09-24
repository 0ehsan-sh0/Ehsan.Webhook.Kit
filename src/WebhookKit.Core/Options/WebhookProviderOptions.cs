// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Core.Options;

/// <summary>Per-provider configuration.</summary>
public sealed class WebhookProviderOptions
{
    /// <summary>Signature settings.</summary>
    public WebhookSignatureOptions Signature { get; } = new();

    /// <summary>Timestamp/replay settings.</summary>
    public WebhookTimestampOptions Timestamp { get; } = new();

    /// <summary>Retry settings.</summary>
    public WebhookRetryOptions Retry { get; } = new();

    /// <summary>Provider-specific body limit; <see langword="null"/> uses the global limit.</summary>
    public long? MaxRequestBodySizeBytes { get; set; }

    /// <summary>Whether a SHA-256 body hash may be used when no event ID is available.</summary>
    public bool AllowBodyHashFallback { get; set; }

    /// <summary>Header carrying the event ID. Null defers to JSON or custom extractors.</summary>
    public string? EventIdHeaderName { get; set; }

    /// <summary>Header carrying the event type. Null defers to JSON/custom extractors.</summary>
    public string? EventTypeHeaderName { get; set; }
}
