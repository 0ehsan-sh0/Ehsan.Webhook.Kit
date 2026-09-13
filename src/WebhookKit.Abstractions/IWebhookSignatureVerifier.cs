// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Cryptographic authentication of the payload. Must use constant-time comparison
/// internally and never leak secrets in results or exceptions.
/// </summary>
public interface IWebhookSignatureVerifier
{
    /// <summary>Verify the request described by <paramref name="context"/>.</summary>
    ValueTask<WebhookVerificationResult> VerifyAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default);
}
