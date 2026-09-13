// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Outcome of signature verification. Expected rejections are returned, not thrown.
/// <see cref="FailureReason"/> never contains secrets.
/// </summary>
public readonly record struct WebhookVerificationResult(bool IsValid, string? FailureReason)
{
    /// <summary>Successful verification.</summary>
    public static WebhookVerificationResult Success() => new(true, null);

    /// <summary>Failed verification with a non-sensitive reason.</summary>
    public static WebhookVerificationResult Fail(string failureReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        return new(false, failureReason);
    }
}
