// Copyright (c) Ehsan. Licensed under the MIT License.
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Core.Options;

namespace WebhookKit.Core.Verifiers;

/// <summary>
/// Cryptographic HMAC signature verifier supporting HMAC-SHA256 and HMAC-SHA512,
/// Hexadecimal and Base64 encodings, secret rotation, and constant-time equality checks.
/// </summary>
public sealed class HmacSignatureVerifier : IWebhookSignatureVerifier
{
    private readonly IOptions<WebhookKitOptions> _options;

    /// <summary>Creates a verifier using the configured provider secrets and signature settings.</summary>
    /// <param name="options">WebhookKit options containing provider signature configuration.</param>
    public HmacSignatureVerifier(IOptions<WebhookKitOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Verifies a provider signature using constant-time comparison.</summary>
    /// <param name="context">Provider name, exact raw body, and request headers; the context contains no secret.</param>
    /// <param name="cancellationToken">Token used to cancel verification.</param>
    /// <returns>A result whose failure reason is safe for diagnostics and HTTP mapping.</returns>
    public ValueTask<WebhookVerificationResult> VerifyAsync(
        WebhookVerificationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Value.Providers.TryGetValue(context.Provider, out var providerOptions) ||
            providerOptions.Signature == null ||
            string.IsNullOrWhiteSpace(providerOptions.Signature.HeaderName) ||
            (string.IsNullOrWhiteSpace(providerOptions.Signature.Secret) &&
             !providerOptions.Signature.AdditionalSecrets.Any(secret => !string.IsNullOrWhiteSpace(secret))))
        {
            return ValueTask.FromResult(
                WebhookVerificationResult.Fail($"Provider '{context.Provider}' is not configured for signature verification."));
        }

        var sigOptions = providerOptions.Signature;

        byte[]? timestampPrefixedInput = null;
        if (sigOptions.Input == WebhookSignatureInput.TimestampPrefixedRawBody)
        {
            if (sigOptions.TimestampSeparator is null)
            {
                return ValueTask.FromResult(WebhookVerificationResult.Fail("Timestamp separator is invalid."));
            }

            var timestampOptions = providerOptions.Timestamp;
            if (string.IsNullOrWhiteSpace(timestampOptions.HeaderName) ||
                !context.Headers.TryGetValue(timestampOptions.HeaderName, out var timestampValues) ||
                timestampValues.Length == 0 ||
                timestampValues[0] is null)
            {
                return ValueTask.FromResult(WebhookVerificationResult.Fail("Timestamp header is missing."));
            }

            var timestampBytes = Encoding.UTF8.GetBytes(timestampValues[0]);
            var separatorBytes = Encoding.UTF8.GetBytes(sigOptions.TimestampSeparator);
            timestampPrefixedInput = new byte[timestampBytes.Length + separatorBytes.Length + context.RawBody.Length];
            timestampBytes.CopyTo(timestampPrefixedInput, 0);
            separatorBytes.CopyTo(timestampPrefixedInput, timestampBytes.Length);
            context.RawBody.CopyTo(timestampPrefixedInput.AsSpan(timestampBytes.Length + separatorBytes.Length));
        }
        else if (sigOptions.Input != WebhookSignatureInput.RawBody)
        {
            return ValueTask.FromResult(WebhookVerificationResult.Fail("Signature input mode is invalid."));
        }

        if (!context.Headers.TryGetValue(sigOptions.HeaderName, out var headerValues) || headerValues.Length == 0)
        {
            return ValueTask.FromResult(
                WebhookVerificationResult.Fail($"Signature header '{sigOptions.HeaderName}' was missing."));
        }

        var rawHeader = headerValues.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
        if (string.IsNullOrEmpty(rawHeader))
        {
            return ValueTask.FromResult(
                WebhookVerificationResult.Fail($"Signature header '{sigOptions.HeaderName}' was empty."));
        }

        // Strip common algorithm or version prefixes (e.g., "sha256=", "sha512=", "v1=")
        string normalizedSignature = StripPrefix(rawHeader);

        byte[] expectedBytes;
        try
        {
            expectedBytes = sigOptions.Encoding switch
            {
                WebhookSignatureEncoding.Hex => Convert.FromHexString(normalizedSignature),
                WebhookSignatureEncoding.Base64 => Convert.FromBase64String(normalizedSignature),
                _ => throw new InvalidOperationException($"Unsupported encoding '{sigOptions.Encoding}'.")
            };
        }
        catch (FormatException)
        {
            return ValueTask.FromResult(WebhookVerificationResult.Fail("Signature verification failed."));
        }

        var secrets = new List<string>();
        if (!string.IsNullOrWhiteSpace(sigOptions.Secret))
        {
            secrets.Add(sigOptions.Secret);
        }

        secrets.AddRange(sigOptions.AdditionalSecrets.Where(secret => !string.IsNullOrWhiteSpace(secret)));

        int hashByteSize = sigOptions.Algorithm == WebhookHashAlgorithm.HmacSha512 ? 64 : 32;
        Span<byte> computedHash = stackalloc byte[hashByteSize];

        foreach (var secret in secrets)
        {
            if (string.IsNullOrWhiteSpace(secret))
            {
                continue;
            }

            byte[] secretBytes = Encoding.UTF8.GetBytes(secret);

            if (sigOptions.Algorithm == WebhookHashAlgorithm.HmacSha256)
            {
                HMACSHA256.HashData(secretBytes, timestampPrefixedInput ?? context.RawBody, computedHash);
            }
            else if (sigOptions.Algorithm == WebhookHashAlgorithm.HmacSha512)
            {
                HMACSHA512.HashData(secretBytes, timestampPrefixedInput ?? context.RawBody, computedHash);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported algorithm '{sigOptions.Algorithm}'.");
            }

            if (CryptographicOperations.FixedTimeEquals(computedHash, expectedBytes))
            {
                return ValueTask.FromResult(WebhookVerificationResult.Success());
            }
        }

        return ValueTask.FromResult(WebhookVerificationResult.Fail("Signature verification failed."));
    }

    private static string StripPrefix(string headerValue)
    {
        ReadOnlySpan<string> prefixes = ["sha256=", "sha512=", "v1=", "v0="];
        foreach (var prefix in prefixes)
        {
            if (headerValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return headerValue[prefix.Length..].Trim();
            }
        }

        return headerValue;
    }
}
