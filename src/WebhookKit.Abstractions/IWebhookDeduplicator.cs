// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Deduplication seam over <see cref="IWebhookStore"/>. Wraps the atomic
/// check-and-insert so pipelines do not reimplement the race-prone
/// exists-then-create pattern.
/// </summary>
public interface IWebhookDeduplicator
{
    /// <summary>
    /// Attempt to acquire processing rights. Returns <c>true</c> on first sight
    /// (caller proceeds), <c>false</c> when already seen (caller returns duplicate response).
    /// </summary>
    ValueTask<bool> TryAcquireAsync(WebhookRecord record, CancellationToken cancellationToken = default);
}
