// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Outcome of provider verification. Expected rejections are returned, not thrown.
/// <see cref="FailureReason"/> never contains secrets.
/// </summary>
/// <param name="IsValid">Whether the request passed verification.</param>
/// <param name="FailureReason">A safe diagnostic reason when verification failed.</param>
/// <param name="ProviderTimestamp">The exact parsed provider timestamp when verification produced one.</param>
public readonly record struct WebhookVerificationResult(
    bool IsValid,
    string? FailureReason,
    DateTimeOffset? ProviderTimestamp = null)
{
    /// <summary>Successful verification.</summary>
    public static WebhookVerificationResult Success() => new(true, null);

    /// <summary>Failed verification with a non-sensitive reason.</summary>
    /// <param name="failureReason">A safe diagnostic reason; secrets and raw payload data are not accepted.</param>
    /// <returns>A failed verification result.</returns>
    public static WebhookVerificationResult Fail(string failureReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        return new(false, failureReason);
    }
}
