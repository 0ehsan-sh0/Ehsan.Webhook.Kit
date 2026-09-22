// Copyright (c) Ehsan. Licensed under the MIT License.
using WebhookKit.Abstractions;

namespace WebhookKit.Core.Extractors;

/// <summary>
/// Chains multiple event type extractors in priority order, returning the first non-null, non-empty extracted type.
/// </summary>
public sealed class CompositeWebhookEventTypeExtractor : IWebhookEventTypeExtractor
{
    private readonly IReadOnlyList<IWebhookEventTypeExtractor> _extractors;

    public CompositeWebhookEventTypeExtractor(IEnumerable<IWebhookEventTypeExtractor> extractors)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        _extractors = extractors.ToList();
    }

    public async ValueTask<string?> ExtractAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var extractor in _extractors)
        {
            string? type = await extractor.ExtractAsync(context, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(type))
            {
                return type;
            }
        }

        return null;
    }
}
