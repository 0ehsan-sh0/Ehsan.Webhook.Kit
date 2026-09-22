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

    public HmacSignatureVerifier(IOptions<WebhookKitOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ValueTask<WebhookVerificationResult> VerifyAsync(
        WebhookVerificationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Value.Providers.TryGetValue(context.Provider, out var providerOptions) ||
            providerOptions.Signature == null ||
            string.IsNullOrWhiteSpace(providerOptions.Signature.HeaderName) ||
            string.IsNullOrWhiteSpace(providerOptions.Signature.Secret))
        {
            return ValueTask.FromResult(
                WebhookVerificationResult.Fail($"Provider '{context.Provider}' is not configured for signature verification."));
        }

        var sigOptions = providerOptions.Signature;

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
            return ValueTask.FromResult(WebhookVerificationResult.Fail("Signature format is invalid."));
        }

        // Collect all active secrets (primary secret + rotation secrets)
        var secrets = new List<string> { sigOptions.Secret };
        if (sigOptions.AdditionalSecrets.Count > 0)
        {
            secrets.AddRange(sigOptions.AdditionalSecrets);
        }

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
                HMACSHA256.HashData(secretBytes, context.RawBody, computedHash);
            }
            else if (sigOptions.Algorithm == WebhookHashAlgorithm.HmacSha512)
            {
                HMACSHA512.HashData(secretBytes, context.RawBody, computedHash);
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

        return ValueTask.FromResult(WebhookVerificationResult.Fail("Signature mismatch."));
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
