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
    /// <param name="record">The candidate record; the store may copy its owned collections.</param>
    /// <param name="cancellationToken">Token used to cancel the atomic claim.</param>
    /// <returns><see langword="true"/> when the caller acquired the record.</returns>
    ValueTask<bool> TryAcquireAsync(WebhookRecord record, CancellationToken cancellationToken = default);
}
