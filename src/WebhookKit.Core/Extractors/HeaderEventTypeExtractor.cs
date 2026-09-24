// Copyright (c) Ehsan. Licensed under the MIT License.
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Core.Options;

namespace WebhookKit.Core.Extractors;

/// <summary>
/// Extracts event type from HTTP request headers using the configured provider header name.
/// </summary>
public sealed class HeaderEventTypeExtractor : IWebhookEventTypeExtractor
{
    private readonly IOptions<WebhookKitOptions> _options;

    /// <summary>Creates an extractor using provider-specific header configuration.</summary>
    /// <param name="options">WebhookKit options containing the event type header name.</param>
    public HeaderEventTypeExtractor(IOptions<WebhookKitOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Extracts the first non-empty configured event type header value.</summary>
    /// <param name="context">Request metadata and exact body.</param>
    /// <param name="cancellationToken">Token reserved for extractor cancellation.</param>
    /// <returns>The event type, or <see langword="null"/> when the header is not configured or present.</returns>
    public ValueTask<string?> ExtractAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Value.Providers.TryGetValue(context.Provider, out var providerOptions) ||
            string.IsNullOrWhiteSpace(providerOptions.EventTypeHeaderName))
        {
            return ValueTask.FromResult<string?>(null);
        }

        if (context.Headers.TryGetValue(providerOptions.EventTypeHeaderName, out var values))
        {
            var value = values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return ValueTask.FromResult<string?>(value);
            }
        }

        return ValueTask.FromResult<string?>(null);
    }
}
