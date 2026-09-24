// Copyright (c) Ehsan. Licensed under the MIT License.
using WebhookKit.Abstractions;

namespace WebhookKit.Core.Extractors;

/// <summary>
/// Chains multiple event ID extractors in priority order, returning the first non-null, non-empty extracted ID.
/// </summary>
public sealed class CompositeWebhookEventIdExtractor : IWebhookEventIdExtractor
{
    private readonly IReadOnlyList<IWebhookEventIdExtractor> _extractors;

    /// <summary>Creates a priority-ordered composite extractor.</summary>
    /// <param name="extractors">Extractors to invoke in order; the sequence is snapshotted.</param>
    public CompositeWebhookEventIdExtractor(IEnumerable<IWebhookEventIdExtractor> extractors)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        _extractors = extractors.ToList();
    }

    /// <summary>Returns the first non-empty event ID produced by the chain.</summary>
    /// <param name="context">Request metadata and exact body.</param>
    /// <param name="cancellationToken">Token passed to each extractor.</param>
    /// <returns>The first extracted event ID, or <see langword="null"/>.</returns>
    public async ValueTask<string?> ExtractAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var extractor in _extractors)
        {
            string? id = await extractor.ExtractAsync(context, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        return null;
    }
}
