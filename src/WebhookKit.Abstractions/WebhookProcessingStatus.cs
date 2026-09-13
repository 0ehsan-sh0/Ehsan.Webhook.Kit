// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Lifecycle state of a webhook record. Numeric values are pinned by contract tests.
/// </summary>
public enum WebhookProcessingStatus
{
    /// <summary>Initial ingress; signature and replay window verified, record created.</summary>
    Received = 0,

    /// <summary>Worker or endpoint currently executing business handlers.</summary>
    Processing = 1,

    /// <summary>Application handler executed successfully to completion.</summary>
    Processed = 2,

    /// <summary>Processing encountered an unrecoverable exception or exceeded retry limits.</summary>
    Failed = 3,

    /// <summary>Webhook was verified and recorded, but no registered handler matched its event type.</summary>
    Ignored = 4,

    /// <summary>Event ID was previously recorded; duplicate response returned without re-executing handlers.</summary>
    Duplicate = 5,
}
