// Copyright (c) Ehsan. Licensed under the MIT License.
using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Core.Options;
using WebhookKit.Core.Verifiers;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.Core.Tests;

public sealed class WebhookTimestampVerifierTests
{
    private const string ProviderName = "stripe";
    private const string HeaderName = "Stripe-Signature-Time";
    private readonly DateTimeOffset _fixedTime = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private (WebhookTimestampVerifier Verifier, FakeWebhookClock Clock) CreateVerifier(Action<WebhookTimestampOptions>? configure = null)
    {
        var clock = new FakeWebhookClock(_fixedTime);
        var options = new WebhookKitOptions();
        options.AddProvider(ProviderName, p =>
        {
            p.Timestamp.HeaderName = HeaderName;
            p.Timestamp.Tolerance = TimeSpan.FromMinutes(5);
            p.Timestamp.AllowMissing = false;
            configure?.Invoke(p.Timestamp);
        });

        var verifier = new WebhookTimestampVerifier(Microsoft.Extensions.Options.Options.Create(options), clock);
        return (verifier, clock);
    }

    [Fact]
    public async Task VerifyAsync_UnixEpochSeconds_WithinTolerance_Succeeds()
    {
        var (verifier, clock) = CreateVerifier();
        // 2 minutes before current time (within 5 min tolerance)
        long epochSeconds = clock.UtcNow.AddMinutes(-2).ToUnixTimeSeconds();

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [epochSeconds.ToString(CultureInfo.InvariantCulture)]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_UnixEpochMilliseconds_WithinTolerance_Succeeds()
    {
        var (verifier, clock) = CreateVerifier();
        long epochMs = clock.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [epochMs.ToString(CultureInfo.InvariantCulture)]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_Iso8601_WithinTolerance_Succeeds()
    {
        var (verifier, clock) = CreateVerifier();
        string isoString = clock.UtcNow.AddMinutes(2).ToString("O"); // 2 minutes future skew, within 5 min tolerance

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [isoString]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_ExpiredPastTolerance_Fails()
    {
        var (verifier, clock) = CreateVerifier();
        // 6 minutes ago (tolerance is 5 minutes)
        long expiredEpoch = clock.UtcNow.AddMinutes(-6).ToUnixTimeSeconds();

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [expiredEpoch.ToString(CultureInfo.InvariantCulture)]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("expired or skewed beyond tolerance");
    }

    [Fact]
    public async Task VerifyAsync_FutureSkewBeyondTolerance_Fails()
    {
        var (verifier, clock) = CreateVerifier();
        // 6 minutes in the future
        long futureEpoch = clock.UtcNow.AddMinutes(6).ToUnixTimeSeconds();

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [futureEpoch.ToString(CultureInfo.InvariantCulture)]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("expired or skewed beyond tolerance");
    }

    [Fact]
    public async Task VerifyAsync_ClockAdvance_ReplaysFail()
    {
        var (verifier, clock) = CreateVerifier();
        long originalTimestamp = clock.UtcNow.ToUnixTimeSeconds();

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [originalTimestamp.ToString(CultureInfo.InvariantCulture)]
            }
        };

        // First check passes
        (await verifier.VerifyAsync(context)).IsValid.Should().BeTrue();

        // Advance clock by 10 minutes
        clock.Advance(TimeSpan.FromMinutes(10));

        // Same webhook replayed now fails
        var replayedResult = await verifier.VerifyAsync(context);
        replayedResult.IsValid.Should().BeFalse();
        replayedResult.FailureReason.Should().Contain("expired or skewed beyond tolerance");
    }

    [Fact]
    public async Task VerifyAsync_MissingHeader_WhenAllowMissingFalse_Fails()
    {
        var (verifier, _) = CreateVerifier(o => o.AllowMissing = false);
        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>()
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("missing");
    }

    [Fact]
    public async Task VerifyAsync_MissingHeader_WhenAllowMissingTrue_Succeeds()
    {
        var (verifier, _) = CreateVerifier(o => o.AllowMissing = true);
        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>()
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_InvalidTimestampFormat_Fails()
    {
        var (verifier, _) = CreateVerifier();
        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = ["not-a-timestamp"]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("invalid");
    }

    [Fact]
    public async Task VerifyAsync_UnconfiguredProvider_Succeeds()
    {
        var (verifier, _) = CreateVerifier();
        var context = new WebhookVerificationContext
        {
            Provider = "unconfigured",
            RawBody = [],
            Headers = new Dictionary<string, string[]>()
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeTrue();
    }
}
