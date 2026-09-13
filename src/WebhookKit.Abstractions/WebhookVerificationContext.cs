// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Inputs to signature verification. Carries no secrets.
/// </summary>
public sealed class WebhookVerificationContext
{
    /// <summary>Configured provider name.</summary>
    public required string Provider { get; init; }

    /// <summary>Exact raw request bytes covered by the signature.</summary>
    public required byte[] RawBody { get; init; }

    /// <summary>Request headers (signature, timestamp, event identity). Multi-value per key.</summary>
    public required IReadOnlyDictionary<string, string[]> Headers { get; init; }
}
