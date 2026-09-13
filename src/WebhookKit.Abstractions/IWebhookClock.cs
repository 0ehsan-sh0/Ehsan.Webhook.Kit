// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Deterministic time provider. All validation, tolerance checks, and storage
/// timestamps must use this instead of <see cref="DateTimeOffset.UtcNow"/> directly.
/// </summary>
public interface IWebhookClock
{
    /// <summary>Current UTC instant.</summary>
    DateTimeOffset UtcNow { get; }
}
