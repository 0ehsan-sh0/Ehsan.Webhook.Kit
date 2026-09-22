// Copyright (c) Ehsan. Licensed under the MIT License.
using WebhookKit.Abstractions;

namespace WebhookKit.Core.Extractors;

/// <summary>
/// Chains multiple event ID extractors in priority order, returning the first non-null, non-empty extracted ID.
/// </summary>
public sealed class CompositeWebhookEventIdExtractor : IWebhookEventIdExtractor
{
    private readonly IReadOnlyList<IWebhookEventIdExtractor> _extractors;

    public CompositeWebhookEventIdExtractor(IEnumerable<IWebhookEventIdExtractor> extractors)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        _extractors = extractors.ToList();
    }

    public async ValueTask<string?> ExtractAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

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
