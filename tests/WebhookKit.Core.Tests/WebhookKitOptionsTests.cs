// Copyright (c) Ehsan. Licensed under the MIT License.
using FluentAssertions;
using Xunit;
using WebhookKit.Core.Options;

namespace WebhookKit.Core.Tests;

public sealed class WebhookKitOptionsTests
{
    [Fact]
    public void Defaults_AreExpected()
    {
        var options = new WebhookKitOptions();

        options.MaxRequestBodySizeBytes.Should().Be(1024 * 1024);
        options.Storage.PersistRawBody.Should().BeTrue();
        options.Storage.DiscardRawBodyAfterSuccessfulSync.Should().BeFalse();
        options.Providers.Should().BeEmpty();
    }

    [Fact]
    public void AddProvider_IsCaseInsensitive_AndRejectsDuplicates()
    {
        var options = new WebhookKitOptions()
            .AddProvider("Stripe", p => p.EventIdHeaderName = "X-Event-Id");

        options.Providers.Should().ContainKey("stripe");

        var act = () => options.AddProvider("STRIPE", _ => { });
        act.Should().Throw<WebhookConfigurationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddProvider_RejectsEmptyNames(string? name)
    {
        var options = new WebhookKitOptions();
        var act = () => options.AddProvider(name!, _ => { });
        act.Should().Throw<WebhookConfigurationException>();
    }

    [Fact]
    public void Validator_AcceptsValidSignatureConfig()
    {
        var options = new WebhookKitOptions();
        options.AddProvider("payments", p =>
        {
            p.Signature.HeaderName = "X-Signature";
            p.Signature.Algorithm = WebhookHashAlgorithm.HmacSha256;
            p.Signature.Secret = "s3cret";
            p.Timestamp.HeaderName = "X-Timestamp";
        });

        var result = new WebhookKitOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validator_RejectsSignatureWithoutSecret_WithoutLeakingSecrets()
    {
        var options = new WebhookKitOptions();
        options.AddProvider("payments", p => p.Signature.HeaderName = "X-Signature");

        var result = new WebhookKitOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("payments");
        result.FailureMessage.Should().NotContain("s3cret");
    }

    [Fact]
    public void Validator_RejectsSecretWithoutHeader()
    {
        var options = new WebhookKitOptions();
        options.AddProvider("payments", p => p.Signature.Secret = "s3cret");

        new WebhookKitOptionsValidator().Validate(null, options).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Validator_RejectsNonPositiveBodySize_AndBadRetry()
    {
        var badBody = new WebhookKitOptions { MaxRequestBodySizeBytes = 0 };
        new WebhookKitOptionsValidator().Validate(null, badBody).Succeeded.Should().BeFalse();

        var badRetry = new WebhookKitOptions();
        badRetry.AddProvider("p", p => p.Retry.MaxAttempts = 0);
        new WebhookKitOptionsValidator().Validate(null, badRetry).Succeeded.Should().BeFalse();

        var badTolerance = new WebhookKitOptions();
        badTolerance.AddProvider("p", p => p.Timestamp.Tolerance = TimeSpan.Zero);
        new WebhookKitOptionsValidator().Validate(null, badTolerance).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Validator_RequiresTimestampConfigurationUnlessExplicitlyAllowed()
    {
        var missing = new WebhookKitOptions();
        missing.AddProvider("payments", _ => { });

        new WebhookKitOptionsValidator().Validate(null, missing).Succeeded.Should().BeFalse();

        var optedOut = new WebhookKitOptions();
        optedOut.AddProvider("payments", provider => provider.Timestamp.AllowMissing = true);

        new WebhookKitOptionsValidator().Validate(null, optedOut).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validator_RejectsNonPositiveProviderBodyLimit()
    {
        var options = new WebhookKitOptions();
        options.AddProvider("payments", provider =>
        {
            provider.MaxRequestBodySizeBytes = 0;
            provider.Timestamp.AllowMissing = true;
        });

        new WebhookKitOptionsValidator().Validate(null, options).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Validator_RequiresTimestampHeaderForTimestampPrefixedSignatures()
    {
        var options = new WebhookKitOptions();
        options.AddProvider("payments", provider =>
        {
            provider.Signature.Input = WebhookSignatureInput.TimestampPrefixedRawBody;
            provider.Timestamp.AllowMissing = true;
        });

        new WebhookKitOptionsValidator().Validate(null, options).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Validator_AcceptsRotationSecrets()
    {
        var options = new WebhookKitOptions();
        options.AddProvider("payments", p =>
        {
            p.Signature.HeaderName = "X-Signature";
            p.Signature.Secret = "current";
            p.Signature.AdditionalSecrets.Add("previous");
            p.Timestamp.AllowMissing = true;
        });

        new WebhookKitOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
    }
}
