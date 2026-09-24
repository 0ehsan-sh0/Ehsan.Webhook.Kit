using System.Security.Cryptography;
using TextEncoding = System.Text.Encoding;
using WebhookKit.Abstractions;
using WebhookKit.Core.Options;

namespace WebhookKit.Testing;

/// <summary>Test utility that creates and verifies provider-compatible HMAC signatures.</summary>
/// <remarks>The generator is intended for tests; it accepts caller-supplied secrets but never logs or exposes them.</remarks>
public sealed class WebhookSignatureGenerator
{
    /// <summary>Default separator for timestamp-prefixed signing input.</summary>
    public const string DefaultTimestampSeparator = ".";
    private const string VerificationFailureReason = "Signature verification failed.";

    private readonly byte[] _primarySecret;
    private readonly byte[][] _verificationSecrets;
    private readonly WebhookHashAlgorithm _algorithm;
    private readonly WebhookSignatureEncoding _encoding;
    private readonly WebhookSignatureInput _input;
    private readonly string _timestampSeparator;

    /// <summary>Creates a generator from explicit algorithm, encoding, and input settings.</summary>
    /// <param name="primarySecret">Secret used to create signatures and the first verification candidate.</param>
    /// <param name="algorithm">HMAC algorithm used for signing and verification.</param>
    /// <param name="encoding">Signature header encoding.</param>
    /// <param name="input">Signing input construction mode.</param>
    /// <param name="timestampSeparator">Separator for timestamp-prefixed input; defaults to <see cref="DefaultTimestampSeparator"/>.</param>
    /// <param name="rotationSecrets">Additional secrets accepted during verification.</param>
    public WebhookSignatureGenerator(
        string primarySecret,
        WebhookHashAlgorithm algorithm = WebhookHashAlgorithm.HmacSha256,
        WebhookSignatureEncoding encoding = WebhookSignatureEncoding.Hex,
        WebhookSignatureInput input = WebhookSignatureInput.RawBody,
        string? timestampSeparator = null,
        IEnumerable<string>? rotationSecrets = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(primarySecret);
        _primarySecret = TextEncoding.UTF8.GetBytes(primarySecret);
        _verificationSecrets = BuildVerificationSecrets(primarySecret, rotationSecrets);
        _algorithm = algorithm;
        _encoding = encoding;
        _input = input;
        _timestampSeparator = timestampSeparator ?? DefaultTimestampSeparator;
        ValidateAlgorithm(_algorithm);
        ValidateEncoding(_encoding);
    }

    /// <summary>Creates a generator from a primary secret and signature options.</summary>
    /// <param name="primarySecret">Secret used to create signatures.</param>
    /// <param name="options">Options supplying algorithm, encoding, input, separator, and rotation secrets.</param>
    /// <param name="rotationSecrets">Additional secrets appended to the options' rotation list.</param>
    public WebhookSignatureGenerator(
        string primarySecret,
        WebhookSignatureOptions options,
        IEnumerable<string>? rotationSecrets = null)
        : this(
            primarySecret,
            GetOptions(options).Algorithm,
            GetOptions(options).Encoding,
            GetOptions(options).Input,
            GetOptions(options).TimestampSeparator,
            GetOptions(options).AdditionalSecrets.Concat(rotationSecrets ?? Array.Empty<string>()))
    {
    }

    /// <summary>Creates a generator from options whose primary secret is configured in <see cref="WebhookSignatureOptions.Secret"/>.</summary>
    /// <param name="options">Signature options containing the primary secret and generation settings.</param>
    public WebhookSignatureGenerator(WebhookSignatureOptions options)
        : this(GetPrimarySecret(options), options ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    /// <summary>Configured HMAC algorithm.</summary>
    public WebhookHashAlgorithm Algorithm => _algorithm;

    /// <summary>Configured signature encoding.</summary>
    public WebhookSignatureEncoding Encoding => _encoding;

    /// <summary>Configured signing input mode.</summary>
    public WebhookSignatureInput Input => _input;

    /// <summary>Compatibility alias for <see cref="Input"/>.</summary>
    public WebhookSignatureInput SigningInput => _input;

    /// <summary>Separator used for timestamp-prefixed signing input.</summary>
    public string TimestampSeparator => _timestampSeparator;

    /// <summary>Generates a signature for an exact body using the configured input mode.</summary>
    /// <param name="rawBody">The exact body bytes; the array is not modified.</param>
    /// <param name="timestamp">Optional timestamp included by timestamp-prefixed modes.</param>
    /// <returns>The encoded signature.</returns>
    public string Generate(byte[] rawBody, string? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        return Generate(rawBody.AsSpan(), timestamp);
    }

    /// <summary>Generates a signature for a body span using the configured input mode.</summary>
    /// <param name="rawBody">The exact body bytes; the span is not modified.</param>
    /// <param name="timestamp">Optional timestamp included by timestamp-prefixed modes.</param>
    /// <returns>The encoded signature.</returns>
    public string Generate(ReadOnlySpan<byte> rawBody, string? timestamp = null)
    {
        return GenerateForInput(rawBody, timestamp, _input, _timestampSeparator);
    }

    /// <summary>Generates a signature with an explicit input mode and separator.</summary>
    /// <param name="rawBody">The exact body bytes.</param>
    /// <param name="timestamp">Timestamp required by timestamp-prefixed modes.</param>
    /// <param name="input">Signing input construction mode.</param>
    /// <param name="timestampSeparator">Optional separator for timestamp-prefixed input.</param>
    /// <returns>The encoded signature.</returns>
    public string Generate(
        byte[] rawBody,
        string timestamp,
        WebhookSignatureInput input,
        string? timestampSeparator = null)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        return GenerateForInput(rawBody.AsSpan(), timestamp, input, timestampSeparator ?? DefaultTimestampSeparator);
    }

    /// <summary>Generates a signature with an explicit input mode and separator.</summary>
    /// <param name="rawBody">The exact body bytes.</param>
    /// <param name="timestamp">Timestamp required by timestamp-prefixed modes.</param>
    /// <param name="input">Signing input construction mode.</param>
    /// <param name="timestampSeparator">Optional separator for timestamp-prefixed input.</param>
    /// <returns>The encoded signature.</returns>
    public string Generate(
        ReadOnlySpan<byte> rawBody,
        string timestamp,
        WebhookSignatureInput input,
        string? timestampSeparator = null)
    {
        return GenerateForInput(rawBody, timestamp, input, timestampSeparator ?? DefaultTimestampSeparator);
    }

    /// <summary>Generates a signature over caller-supplied signing input.</summary>
    /// <param name="signingInput">Bytes already arranged in the provider's signing format.</param>
    /// <returns>The encoded signature.</returns>
    public string GenerateCustom(byte[] signingInput)
    {
        ArgumentNullException.ThrowIfNull(signingInput);
        return GenerateCustom(signingInput.AsSpan());
    }

    /// <summary>Generates a signature over caller-supplied signing input.</summary>
    /// <param name="signingInput">Bytes already arranged in the provider's signing format.</param>
    /// <returns>The encoded signature.</returns>
    public string GenerateCustom(ReadOnlySpan<byte> signingInput)
    {
        return Encode(ComputeHash(_primarySecret, signingInput));
    }

    /// <summary>Generates a signature using a caller-defined signing-input transform.</summary>
    /// <param name="rawBody">The exact request body supplied to the transform.</param>
    /// <param name="timestamp">Optional timestamp supplied to the transform.</param>
    /// <param name="customSigningInput">Transform that returns the exact bytes to sign.</param>
    /// <returns>The encoded signature.</returns>
    public string GenerateWithCustomInput(
        byte[] rawBody,
        string? timestamp,
        Func<ReadOnlyMemory<byte>, string?, ReadOnlyMemory<byte>> customSigningInput)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        ArgumentNullException.ThrowIfNull(customSigningInput);
        var input = customSigningInput(rawBody.ToArray(), timestamp);
        return GenerateCustom(input.Span);
    }

    /// <summary>Generates a signature using a caller-defined signing-input transform.</summary>
    /// <param name="rawBody">The exact request body supplied to the transform.</param>
    /// <param name="timestamp">Optional timestamp supplied to the transform.</param>
    /// <param name="customSigningInput">Transform that returns the exact bytes to sign.</param>
    /// <returns>The encoded signature.</returns>
    public string GenerateWithCustomInput(
        ReadOnlySpan<byte> rawBody,
        string? timestamp,
        Func<ReadOnlyMemory<byte>, string?, ReadOnlyMemory<byte>> customSigningInput)
    {
        ArgumentNullException.ThrowIfNull(customSigningInput);
        var input = customSigningInput(rawBody.ToArray(), timestamp);
        return GenerateCustom(input.Span);
    }

    /// <summary>Compatibility alias for generating over explicit signing input.</summary>
    /// <param name="signingInput">Bytes already arranged in the provider's signing format.</param>
    /// <returns>The encoded signature.</returns>
    public string GenerateFromSigningInput(byte[] signingInput)
    {
        return GenerateCustom(signingInput);
    }

    /// <summary>Verifies a signature using the configured input mode.</summary>
    /// <param name="signature">Encoded signature to check.</param>
    /// <param name="rawBody">The exact body bytes.</param>
    /// <param name="timestamp">Optional timestamp included by the configured input mode.</param>
    /// <returns><see langword="true"/> when a configured secret matches.</returns>
    public bool Verify(string signature, byte[] rawBody, string? timestamp = null)
    {
        return Verify(signature, rawBody.AsSpan(), timestamp);
    }

    /// <summary>Verifies a signature using the configured input mode.</summary>
    /// <param name="signature">Encoded signature to check.</param>
    /// <param name="rawBody">The exact body bytes.</param>
    /// <param name="timestamp">Optional timestamp included by the configured input mode.</param>
    /// <returns><see langword="true"/> when a configured secret matches.</returns>
    public bool Verify(string signature, ReadOnlySpan<byte> rawBody, string? timestamp = null)
    {
        return VerifyResult(signature, rawBody, timestamp).IsValid;
    }

    /// <summary>Compatibility overload with body-first argument order.</summary>
    /// <param name="rawBody">The exact body bytes.</param>
    /// <param name="signature">Encoded signature to check.</param>
    /// <param name="timestamp">Optional timestamp included by the configured input mode.</param>
    /// <returns><see langword="true"/> when a configured secret matches.</returns>
    public bool Verify(byte[] rawBody, string signature, string? timestamp = null)
    {
        return Verify(signature, rawBody, timestamp);
    }

    /// <summary>Verifies a signature with explicit input settings.</summary>
    /// <param name="signature">Encoded signature to check.</param>
    /// <param name="rawBody">The exact body bytes.</param>
    /// <param name="timestamp">Timestamp required by timestamp-prefixed modes.</param>
    /// <param name="input">Signing input construction mode.</param>
    /// <param name="timestampSeparator">Optional separator for timestamp-prefixed input.</param>
    /// <returns><see langword="true"/> when a configured secret matches.</returns>
    public bool Verify(
        string signature,
        byte[] rawBody,
        string timestamp,
        WebhookSignatureInput input,
        string? timestampSeparator = null)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        return VerifyResult(signature, rawBody.AsSpan(), timestamp, input, timestampSeparator).IsValid;
    }

    /// <summary>Verifies a signature with explicit input settings.</summary>
    /// <param name="signature">Encoded signature to check.</param>
    /// <param name="rawBody">The exact body bytes.</param>
    /// <param name="timestamp">Timestamp required by timestamp-prefixed modes.</param>
    /// <param name="input">Signing input construction mode.</param>
    /// <param name="timestampSeparator">Optional separator for timestamp-prefixed input.</param>
    /// <returns><see langword="true"/> when a configured secret matches.</returns>
    public bool Verify(
        string signature,
        ReadOnlySpan<byte> rawBody,
        string timestamp,
        WebhookSignatureInput input,
        string? timestampSeparator = null)
    {
        return VerifyResult(signature, rawBody, timestamp, input, timestampSeparator).IsValid;
    }

    /// <summary>Verifies a signature over caller-supplied signing input.</summary>
    /// <param name="signature">Encoded signature to check.</param>
    /// <param name="signingInput">Bytes already arranged in the provider's signing format.</param>
    /// <returns><see langword="true"/> when a configured secret matches.</returns>
    public bool VerifyCustom(string signature, byte[] signingInput)
    {
        ArgumentNullException.ThrowIfNull(signingInput);
        return VerifyCustom(signature, signingInput.AsSpan());
    }

    /// <summary>Verifies a signature over caller-supplied signing input.</summary>
    /// <param name="signature">Encoded signature to check.</param>
    /// <param name="signingInput">Bytes already arranged in the provider's signing format.</param>
    /// <returns><see langword="true"/> when a configured secret matches.</returns>
    public bool VerifyCustom(string signature, ReadOnlySpan<byte> signingInput)
    {
        return VerifyBytes(signature, signingInput).IsValid;
    }

    /// <summary>Verifies a signature using a caller-defined signing-input transform.</summary>
    /// <param name="signature">Encoded signature to check.</param>
    /// <param name="rawBody">The exact body bytes supplied to the transform.</param>
    /// <param name="timestamp">Optional timestamp supplied to the transform.</param>
    /// <param name="customSigningInput">Transform that returns the exact bytes to verify.</param>
    /// <returns><see langword="true"/> when a configured secret matches.</returns>
    public bool VerifyWithCustomInput(
        string signature,
        byte[] rawBody,
        string? timestamp,
        Func<ReadOnlyMemory<byte>, string?, ReadOnlyMemory<byte>> customSigningInput)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        ArgumentNullException.ThrowIfNull(customSigningInput);
        var input = customSigningInput(rawBody.ToArray(), timestamp);
        return VerifyCustom(signature, input.Span);
    }

    /// <summary>Verifies a signature and returns a structured result.</summary>
    /// <param name="signature">Encoded signature to check.</param>
    /// <param name="rawBody">The exact body bytes.</param>
    /// <param name="timestamp">Optional timestamp included by the configured input mode.</param>
    /// <param name="cancellationToken">Token used to cancel verification.</param>
    /// <returns>A result whose failure reason is safe and contains no secret data.</returns>
    public WebhookVerificationResult VerifyResult(
        string signature,
        byte[] rawBody,
        string? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        return VerifyResult(signature, rawBody.AsSpan(), timestamp, cancellationToken);
    }

    /// <summary>Verifies a signature and returns a structured result.</summary>
    /// <param name="signature">Encoded signature to check.</param>
    /// <param name="rawBody">The exact body bytes.</param>
    /// <param name="timestamp">Optional timestamp included by the configured input mode.</param>
    /// <param name="cancellationToken">Token used to cancel verification.</param>
    /// <returns>A result whose failure reason is safe and contains no secret data.</returns>
    public WebhookVerificationResult VerifyResult(
        string signature,
        ReadOnlySpan<byte> rawBody,
        string? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        return VerifyResult(signature, rawBody, timestamp, _input, _timestampSeparator, cancellationToken);
    }

    /// <summary>Verifies a signature with explicit input settings and returns a structured result.</summary>
    /// <param name="signature">Encoded signature to check.</param>
    /// <param name="rawBody">The exact body bytes.</param>
    /// <param name="timestamp">Timestamp required by timestamp-prefixed modes.</param>
    /// <param name="input">Signing input construction mode.</param>
    /// <param name="timestampSeparator">Optional separator for timestamp-prefixed input.</param>
    /// <param name="cancellationToken">Token used to cancel verification.</param>
    /// <returns>A result whose failure reason is safe and contains no secret data.</returns>
    public WebhookVerificationResult VerifyResult(
        string signature,
        ReadOnlySpan<byte> rawBody,
        string? timestamp,
        WebhookSignatureInput input,
        string? timestampSeparator = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var signingInput = BuildSigningInput(rawBody, timestamp, input, timestampSeparator ?? DefaultTimestampSeparator);
        return VerifyBytes(signature, signingInput, cancellationToken);
    }

    /// <summary>Verifies caller-supplied signing input and returns a structured result.</summary>
    /// <param name="signature">Encoded signature to check.</param>
    /// <param name="signingInput">Bytes already arranged in the provider's signing format.</param>
    /// <param name="cancellationToken">Token used to cancel verification.</param>
    /// <returns>A result whose failure reason is safe and contains no secret data.</returns>
    public WebhookVerificationResult VerifyCustomResult(
        string signature,
        byte[] signingInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signingInput);
        return VerifyCustomResult(signature, signingInput.AsSpan(), cancellationToken);
    }

    /// <summary>Verifies caller-supplied signing input and returns a structured result.</summary>
    /// <param name="signature">Encoded signature to check.</param>
    /// <param name="signingInput">Bytes already arranged in the provider's signing format.</param>
    /// <param name="cancellationToken">Token used to cancel verification.</param>
    /// <returns>A result whose failure reason is safe and contains no secret data.</returns>
    public WebhookVerificationResult VerifyCustomResult(
        string signature,
        ReadOnlySpan<byte> signingInput,
        CancellationToken cancellationToken = default)
    {
        return VerifyBytes(signature, signingInput, cancellationToken);
    }

    /// <summary>Verifies a caller-defined signing input and returns a structured result.</summary>
    /// <param name="signature">Encoded signature to check.</param>
    /// <param name="rawBody">The exact body bytes supplied to the transform.</param>
    /// <param name="timestamp">Optional timestamp supplied to the transform.</param>
    /// <param name="customSigningInput">Transform that returns the exact bytes to verify.</param>
    /// <param name="cancellationToken">Token used to cancel verification.</param>
    /// <returns>A result whose failure reason is safe and contains no secret data.</returns>
    public WebhookVerificationResult VerifyWithCustomInput(
        string signature,
        byte[] rawBody,
        string? timestamp,
        Func<ReadOnlyMemory<byte>, string?, ReadOnlyMemory<byte>> customSigningInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        ArgumentNullException.ThrowIfNull(customSigningInput);
        var input = customSigningInput(rawBody, timestamp);
        return VerifyCustomResult(signature, input.Span, cancellationToken);
    }

    /// <summary>Returns the utility type name without exposing configuration or secrets.</summary>
    /// <returns>The type name.</returns>
    public override string ToString()
    {
        return nameof(WebhookSignatureGenerator);
    }

    private static WebhookSignatureOptions GetOptions(WebhookSignatureOptions options)
    {
        return options ?? throw new ArgumentNullException(nameof(options));
    }

    private static string GetPrimarySecret(WebhookSignatureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.Secret))
        {
            throw new ArgumentException("A primary secret is required.", nameof(options));
        }

        return options.Secret;
    }

    private static byte[][] BuildVerificationSecrets(string primarySecret, IEnumerable<string>? rotationSecrets)
    {
        var secrets = new List<byte[]> { TextEncoding.UTF8.GetBytes(primarySecret) };
        if (rotationSecrets is null)
        {
            return secrets.ToArray();
        }

        foreach (var secret in rotationSecrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                secrets.Add(TextEncoding.UTF8.GetBytes(secret));
            }
        }

        return secrets.ToArray();
    }

    private static void ValidateAlgorithm(WebhookHashAlgorithm algorithm)
    {
        if (algorithm is not WebhookHashAlgorithm.HmacSha256 and not WebhookHashAlgorithm.HmacSha512)
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm));
        }
    }

    private static void ValidateEncoding(WebhookSignatureEncoding encoding)
    {
        if (encoding is not WebhookSignatureEncoding.Hex and not WebhookSignatureEncoding.Base64)
        {
            throw new ArgumentOutOfRangeException(nameof(encoding));
        }
    }

    private static byte[] BuildSigningInput(
        ReadOnlySpan<byte> rawBody,
        string? timestamp,
        WebhookSignatureInput input,
        string separator)
    {
        switch (input)
        {
            case WebhookSignatureInput.RawBody:
                return rawBody.ToArray();
            case WebhookSignatureInput.TimestampPrefixedRawBody:
                ArgumentNullException.ThrowIfNull(timestamp);

                var timestampBytes = TextEncoding.UTF8.GetBytes(timestamp);
                var separatorBytes = TextEncoding.UTF8.GetBytes(separator);
                var result = new byte[timestampBytes.Length + separatorBytes.Length + rawBody.Length];
                timestampBytes.CopyTo(result, 0);
                separatorBytes.CopyTo(result, timestampBytes.Length);
                rawBody.CopyTo(result.AsSpan(timestampBytes.Length + separatorBytes.Length));
                return result;
            default:
                throw new ArgumentOutOfRangeException(nameof(input));
        }
    }

    private string GenerateForInput(
        ReadOnlySpan<byte> rawBody,
        string? timestamp,
        WebhookSignatureInput input,
        string separator)
    {
        var signingInput = BuildSigningInput(rawBody, timestamp, input, separator);
        return Encode(ComputeHash(_primarySecret, signingInput));
    }

    private byte[] ComputeHash(byte[] secret, ReadOnlySpan<byte> input)
    {
        var hashSize = _algorithm == WebhookHashAlgorithm.HmacSha512 ? 64 : 32;
        var hash = new byte[hashSize];
        ComputeHash(secret, input, hash);
        return hash;
    }

    private string Encode(ReadOnlySpan<byte> hash)
    {
        return _encoding == WebhookSignatureEncoding.Hex
            ? Convert.ToHexString(hash).ToLowerInvariant()
            : Convert.ToBase64String(hash);
    }

    private WebhookVerificationResult VerifyBytes(
        string signature,
        ReadOnlySpan<byte> signingInput,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryDecodeSignature(signature, out var suppliedHash))
        {
            return WebhookVerificationResult.Fail(VerificationFailureReason);
        }

        var computedHash = new byte[_algorithm == WebhookHashAlgorithm.HmacSha512 ? 64 : 32];
        var valid = false;
        foreach (var secret in _verificationSecrets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ComputeHash(secret, signingInput, computedHash);
            valid |= CryptographicOperations.FixedTimeEquals(computedHash, suppliedHash);
        }

        return valid
            ? WebhookVerificationResult.Success()
            : WebhookVerificationResult.Fail(VerificationFailureReason);
    }

    private void ComputeHash(byte[] secret, ReadOnlySpan<byte> input, Span<byte> destination)
    {
        if (_algorithm == WebhookHashAlgorithm.HmacSha256)
        {
            HMACSHA256.HashData(secret, input, destination);
        }
        else
        {
            HMACSHA512.HashData(secret, input, destination);
        }
    }

    private bool TryDecodeSignature(string? signature, out byte[] decoded)
    {
        decoded = [];
        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        var normalized = StripPrefix(signature.Trim());
        if (normalized.Length == 0)
        {
            return false;
        }

        try
        {
            decoded = _encoding == WebhookSignatureEncoding.Hex
                ? Convert.FromHexString(normalized)
                : Convert.FromBase64String(normalized);
            return decoded.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string StripPrefix(string signature)
    {
        ReadOnlySpan<string> prefixes = ["sha256=", "sha512=", "v1=", "v0="];
        foreach (var prefix in prefixes)
        {
            if (signature.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return signature[prefix.Length..].Trim();
            }
        }

        return signature;
    }
}
