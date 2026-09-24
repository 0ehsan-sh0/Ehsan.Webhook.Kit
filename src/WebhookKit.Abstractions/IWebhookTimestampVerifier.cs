// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Validates that incoming webhook timestamps conform to configured replay tolerance windows
/// using an <see cref="IWebhookClock"/>, rejecting replayed or future-skewed requests.
/// </summary>
public interface IWebhookTimestampVerifier
{
    /// <summary>Verify the request timestamp described by <paramref name="context"/>.</summary>
    /// <param name="context">Provider name, exact raw bytes, and headers.</param>
    /// <param name="cancellationToken">Token used to cancel timestamp validation.</param>
    /// <returns>A safe result for expected acceptance or replay rejection; successful results carry the exact parsed provider timestamp.</returns>
    ValueTask<WebhookVerificationResult> VerifyAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default);
}
