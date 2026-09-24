namespace WebhookKit.Core.Options;

/// <summary>Defines which bytes are covered by a provider signature.</summary>
public enum WebhookSignatureInput
{
    /// <summary>Signs only the exact raw request body.</summary>
    RawBody = 0,
    /// <summary>Signs the provider timestamp, separator, and exact raw request body.</summary>
    TimestampPrefixedRawBody = 1,
    /// <summary>Compatibility alias for <see cref="RawBody"/>.</summary>
    RawBodyOnly = RawBody,
    /// <summary>Compatibility alias for <see cref="TimestampPrefixedRawBody"/>.</summary>
    TimestampPrefixed = TimestampPrefixedRawBody
}
