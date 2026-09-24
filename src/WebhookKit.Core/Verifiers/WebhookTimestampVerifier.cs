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
internal sealed class WebhookTimestampVerifier : IWebhookTimestampVerifier
{
    private readonly IOptions<WebhookKitOptions> _options;
    private readonly IWebhookClock _clock;

    /// <summary>Creates a timestamp verifier using configured tolerance and an injectable clock.</summary>
    /// <param name="options">WebhookKit options containing provider timestamp configuration.</param>
    /// <param name="clock">Clock used to compare the provider timestamp with current UTC time.</param>
    public WebhookTimestampVerifier(IOptions<WebhookKitOptions> options, IWebhookClock clock)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Validates timestamp freshness and replay tolerance.</summary>
    /// <param name="context">Provider name, exact raw body, and request headers.</param>
    /// <param name="cancellationToken">Token used to cancel validation.</param>
    /// <returns>A result whose rejection reason contains no secrets or raw payload data.</returns>
    public ValueTask<WebhookVerificationResult> VerifyAsync(
        WebhookVerificationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Value.Providers.TryGetValue(context.Provider, out var providerOptions) ||
            providerOptions.Timestamp == null)
        {
            return ValueTask.FromResult(WebhookVerificationResult.Fail("Timestamp verification is not configured."));
        }

        var timestampOptions = providerOptions.Timestamp;
        if (string.IsNullOrWhiteSpace(timestampOptions.HeaderName))
        {
            return ValueTask.FromResult(timestampOptions.AllowMissing
                ? WebhookVerificationResult.Success()
                : WebhookVerificationResult.Fail("Timestamp verification is not configured."));
        }

        if (!context.Headers.TryGetValue(timestampOptions.HeaderName, out var headerValues) || headerValues.Count == 0)
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
            try
            {
                timestamp = epochValue < 10_000_000_000L
                    ? DateTimeOffset.FromUnixTimeSeconds(epochValue)
                    : DateTimeOffset.FromUnixTimeMilliseconds(epochValue);
            }
            catch (ArgumentOutOfRangeException)
            {
                return ValueTask.FromResult(WebhookVerificationResult.Fail("Timestamp format is invalid."));
            }
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
