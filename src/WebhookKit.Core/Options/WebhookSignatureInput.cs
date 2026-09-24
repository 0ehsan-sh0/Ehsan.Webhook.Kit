namespace WebhookKit.Core.Options;

public enum WebhookSignatureInput
{
    RawBody = 0,
    TimestampPrefixedRawBody = 1,
    RawBodyOnly = RawBody,
    TimestampPrefixed = TimestampPrefixedRawBody
}
