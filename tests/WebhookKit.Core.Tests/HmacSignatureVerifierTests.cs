// Copyright (c) Ehsan. Licensed under the MIT License.
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Core.Options;
using WebhookKit.Core.Verifiers;
using Xunit;

namespace WebhookKit.Core.Tests;

public sealed class HmacSignatureVerifierTests
{
    private const string ProviderName = "github";
    private const string HeaderName = "X-Hub-Signature-256";
    private const string Secret = "super-secret-key-123";

    private static HmacSignatureVerifier CreateVerifier(Action<WebhookProviderOptions>? configure = null)
    {
        var options = new WebhookKitOptions();
        options.AddProvider(ProviderName, p =>
        {
            p.Signature.HeaderName = HeaderName;
            p.Signature.Secret = Secret;
            p.Signature.Algorithm = WebhookHashAlgorithm.HmacSha256;
            p.Signature.Encoding = WebhookSignatureEncoding.Hex;
            configure?.Invoke(p);
        });

        return new HmacSignatureVerifier(Microsoft.Extensions.Options.Options.Create(options));
    }

    private static string ComputeSignature(byte[] body, string secret, WebhookHashAlgorithm algorithm, WebhookSignatureEncoding encoding)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(secret);
        byte[] hash = algorithm == WebhookHashAlgorithm.HmacSha256
            ? HMACSHA256.HashData(keyBytes, body)
            : HMACSHA512.HashData(keyBytes, body);

        return encoding == WebhookSignatureEncoding.Hex
            ? Convert.ToHexString(hash).ToLowerInvariant()
            : Convert.ToBase64String(hash);
    }

    [Fact]
    public async Task VerifyAsync_NullContext_ThrowsArgumentNullException()
    {
        var verifier = CreateVerifier();
        var act = async () => await verifier.VerifyAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task VerifyAsync_UnconfiguredProvider_Fails()
    {
        var verifier = CreateVerifier();
        var context = new WebhookVerificationContext
        {
            Provider = "unknown",
            RawBody = Encoding.UTF8.GetBytes("{}"),
            Headers = new Dictionary<string, string[]>()
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("unknown");
    }

    [Fact]
    public async Task VerifyAsync_MissingHeader_Fails()
    {
        var verifier = CreateVerifier();
        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = Encoding.UTF8.GetBytes("{}"),
            Headers = new Dictionary<string, string[]>()
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("missing");
    }

    [Fact]
    public async Task VerifyAsync_EmptyHeader_Fails()
    {
        var verifier = CreateVerifier();
        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = Encoding.UTF8.GetBytes("{}"),
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = ["   "]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("empty");
    }

    [Fact]
    public async Task VerifyAsync_InvalidFormat_Fails()
    {
        var verifier = CreateVerifier();
        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = Encoding.UTF8.GetBytes("{}"),
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = ["not-a-valid-hex-or-base64!@#$"]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be("Signature verification failed.");
    }

    [Fact]
    public async Task VerifyAsync_HmacSha256_Hex_Valid_Succeeds()
    {
        var verifier = CreateVerifier();
        byte[] body = Encoding.UTF8.GetBytes("{\"action\":\"created\"}");
        string signature = ComputeSignature(body, Secret, WebhookHashAlgorithm.HmacSha256, WebhookSignatureEncoding.Hex);

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [signature]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeTrue();
        result.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_HmacSha256_Base64_Valid_Succeeds()
    {
        var verifier = CreateVerifier(p => p.Signature.Encoding = WebhookSignatureEncoding.Base64);
        byte[] body = Encoding.UTF8.GetBytes("{\"action\":\"created\"}");
        string signature = ComputeSignature(body, Secret, WebhookHashAlgorithm.HmacSha256, WebhookSignatureEncoding.Base64);

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [signature]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_HmacSha512_Hex_Valid_Succeeds()
    {
        var verifier = CreateVerifier(p =>
        {
            p.Signature.Algorithm = WebhookHashAlgorithm.HmacSha512;
            p.Signature.Encoding = WebhookSignatureEncoding.Hex;
        });

        byte[] body = Encoding.UTF8.GetBytes("{\"action\":\"created\"}");
        string signature = ComputeSignature(body, Secret, WebhookHashAlgorithm.HmacSha512, WebhookSignatureEncoding.Hex);

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [signature]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_HmacSha512_Base64_Valid_Succeeds()
    {
        var verifier = CreateVerifier(p =>
        {
            p.Signature.Algorithm = WebhookHashAlgorithm.HmacSha512;
            p.Signature.Encoding = WebhookSignatureEncoding.Base64;
        });

        byte[] body = Encoding.UTF8.GetBytes("{\"action\":\"created\"}");
        string signature = ComputeSignature(body, Secret, WebhookHashAlgorithm.HmacSha512, WebhookSignatureEncoding.Base64);

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [signature]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("sha256=")]
    [InlineData("v1=")]
    [InlineData("SHA256=")]
    public async Task VerifyAsync_PrefixStripping_Succeeds(string prefix)
    {
        var verifier = CreateVerifier();
        byte[] body = Encoding.UTF8.GetBytes("{\"action\":\"opened\"}");
        string hexSignature = ComputeSignature(body, Secret, WebhookHashAlgorithm.HmacSha256, WebhookSignatureEncoding.Hex);

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [$"{prefix}{hexSignature}"]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_SecretRotation_SecondarySecretValid_Succeeds()
    {
        const string oldSecret = "old-deprecated-secret";
        const string currentSecret = "current-active-secret";

        var verifier = CreateVerifier(p =>
        {
            p.Signature.Secret = currentSecret; // Primary secret
            p.Signature.AdditionalSecrets.Add(oldSecret); // Rotation fallback
        });

        byte[] body = Encoding.UTF8.GetBytes("{\"event\":\"transfer\"}");
        // Signature generated using the old secret
        string signature = ComputeSignature(body, oldSecret, WebhookHashAlgorithm.HmacSha256, WebhookSignatureEncoding.Hex);

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [signature]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_AdditionalSecretWithoutPrimary_Succeeds()
    {
        var verifier = CreateVerifier(provider =>
        {
            provider.Signature.Secret = null!;
            provider.Signature.AdditionalSecrets.Add(Secret);
        });
        byte[] body = Encoding.UTF8.GetBytes("{\"event\":\"transfer\"}");
        string signature = ComputeSignature(body, Secret, WebhookHashAlgorithm.HmacSha256, WebhookSignatureEncoding.Hex);
        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [signature]
            }
        };

        var result = await verifier.VerifyAsync(context);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_WrongSecret_Fails()
    {
        var verifier = CreateVerifier();
        byte[] body = Encoding.UTF8.GetBytes("{\"event\":\"transfer\"}");
        string signature = ComputeSignature(body, "wrong-secret-key", WebhookHashAlgorithm.HmacSha256, WebhookSignatureEncoding.Hex);

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [signature]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be("Signature verification failed.");
    }

    [Fact]
    public async Task VerifyAsync_EmptyBody_ValidSignature_Succeeds()
    {
        var verifier = CreateVerifier();
        byte[] emptyBody = [];
        string signature = ComputeSignature(emptyBody, Secret, WebhookHashAlgorithm.HmacSha256, WebhookSignatureEncoding.Hex);

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = emptyBody,
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [signature]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_Utf8UnicodePayload_ValidSignature_Succeeds()
    {
        var verifier = CreateVerifier();
        byte[] unicodeBody = Encoding.UTF8.GetBytes("{\"greeting\":\"سلام دنیا 🚀\",\"currency\":\"€\"}");
        string signature = ComputeSignature(unicodeBody, Secret, WebhookHashAlgorithm.HmacSha256, WebhookSignatureEncoding.Hex);

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = unicodeBody,
            Headers = new Dictionary<string, string[]>
            {
                [HeaderName] = [signature]
            }
        };

        var result = await verifier.VerifyAsync(context);
        result.IsValid.Should().BeTrue();
    }
}
