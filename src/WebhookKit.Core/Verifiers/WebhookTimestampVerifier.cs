// Copyright (c) Ehsan. Licensed under the MIT License.
using System.Globalization;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Core.Options;

namespace WebhookKit.Core.Verifiers;

/// <summary>
/// Replay-attack prevention verifier validating provider timestamp headers
/// against a configured tolerance window using <see cref="IWebhookClock"/>.
/// </summary>
public sealed class WebhookTimestampVerifier : IWebhookTimestampVerifier
{
    private readonly IOptions<WebhookKitOptions> _options;
    private readonly IWebhookClock _clock;

    public WebhookTimestampVerifier(IOptions<WebhookKitOptions> options, IWebhookClock clock)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask<WebhookVerificationResult> VerifyAsync(
        WebhookVerificationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Value.Providers.TryGetValue(context.Provider, out var providerOptions) ||
            providerOptions.Timestamp == null ||
            string.IsNullOrWhiteSpace(providerOptions.Timestamp.HeaderName))
        {
            // Timestamp validation not configured for this provider
            return ValueTask.FromResult(WebhookVerificationResult.Success());
        }

        var timestampOptions = providerOptions.Timestamp;

        if (!context.Headers.TryGetValue(timestampOptions.HeaderName, out var headerValues) || headerValues.Length == 0)
        {
            return ValueTask.FromResult(timestampOptions.AllowMissing
                ? WebhookVerificationResult.Success()
                : WebhookVerificationResult.Fail($"Timestamp header '{timestampOptions.HeaderName}' was missing."));
        }

        var rawTimestamp = headerValues.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
        if (string.IsNullOrEmpty(rawTimestamp))
        {
            return ValueTask.FromResult(timestampOptions.AllowMissing
                ? WebhookVerificationResult.Success()
                : WebhookVerificationResult.Fail($"Timestamp header '{timestampOptions.HeaderName}' was empty."));
        }

        DateTimeOffset timestamp;
        if (long.TryParse(rawTimestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long epochValue))
        {
            // If epochValue < 10,000,000,000 (roughly Nov 2286 in seconds), treat as seconds; else milliseconds
            timestamp = epochValue < 10_000_000_000L
                ? DateTimeOffset.FromUnixTimeSeconds(epochValue)
                : DateTimeOffset.FromUnixTimeMilliseconds(epochValue);
        }
        else if (DateTimeOffset.TryParse(rawTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDto))
        {
            timestamp = parsedDto;
        }
        else
        {
            return ValueTask.FromResult(WebhookVerificationResult.Fail("Timestamp format is invalid."));
        }

        var now = _clock.UtcNow;
        var skew = (now - timestamp).Duration();

        if (skew > timestampOptions.Tolerance)
        {
            return ValueTask.FromResult(
                WebhookVerificationResult.Fail($"Timestamp expired or skewed beyond tolerance window ({skew.TotalSeconds:F0}s > {timestampOptions.Tolerance.TotalSeconds:F0}s)."));
        }

        return ValueTask.FromResult(WebhookVerificationResult.Success());
    }
}
