// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Cryptographic authentication of the payload. Must use constant-time comparison
/// internally and never leak secrets in results or exceptions.
/// </summary>
public interface IWebhookSignatureVerifier
{
    /// <summary>Verify the request described by <paramref name="context"/>.</summary>
    /// <param name="context">Provider name, exact raw bytes, and headers; it contains no secret.</param>
    /// <param name="cancellationToken">Token used to cancel verification.</param>
    /// <returns>A safe result for expected acceptance or rejection.</returns>
    ValueTask<WebhookVerificationResult> VerifyAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default);
}
