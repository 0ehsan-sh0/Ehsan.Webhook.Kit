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

    public long? MaxRequestBodySizeBytes { get; set; }

    public bool AllowBodyHashFallback { get; set; }

    /// <summary>Header carrying the event ID. Null defers to JSON/custom extractors (Tasks 08/09).</summary>
    public string? EventIdHeaderName { get; set; }

    /// <summary>Header carrying the event type. Null defers to JSON/custom extractors.</summary>
    public string? EventTypeHeaderName { get; set; }
}
