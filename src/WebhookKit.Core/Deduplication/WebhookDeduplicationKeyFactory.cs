using System.Security.Cryptography;
using System.Text;
using WebhookKit.Core.Options;

namespace WebhookKit.Core.Deduplication;

public sealed class WebhookDeduplicationKeyFactory
{
    private const byte FallbackSeparator = 0x00;
    private readonly Encoding _utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public string Create(string provider, string? eventId, ReadOnlyMemory<byte> rawBody, WebhookProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalizedProvider = NormalizeProvider(provider);

        if (!string.IsNullOrWhiteSpace(eventId))
        {
            return $"{normalizedProvider}:{eventId}";
        }

        if (!options.AllowBodyHashFallback)
        {
            throw new WebhookConfigurationException("An Event ID is required for deduplication when body-hash fallback is disabled.");
        }

        var providerBytes = _utf8.GetBytes(normalizedProvider);
        var input = new byte[providerBytes.Length + 1 + rawBody.Length];
        providerBytes.CopyTo(input, 0);
        input[providerBytes.Length] = FallbackSeparator;
        rawBody.Span.CopyTo(input.AsSpan(providerBytes.Length + 1));
        var hash = SHA256.HashData(input);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{normalizedProvider}:sha256:{hex}";
    }

    private static string NormalizeProvider(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("Provider must be non-empty.", nameof(provider));
        }

        return provider.Trim().ToLowerInvariant();
    }
}
