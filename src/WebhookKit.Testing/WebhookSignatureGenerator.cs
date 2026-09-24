using System.Security.Cryptography;
using TextEncoding = System.Text.Encoding;
using WebhookKit.Abstractions;
using WebhookKit.Core.Options;

namespace WebhookKit.Testing;

public sealed class WebhookSignatureGenerator
{
    public const string DefaultTimestampSeparator = ".";
    private const string VerificationFailureReason = "Signature verification failed.";

    private readonly byte[] _primarySecret;
    private readonly byte[][] _verificationSecrets;
    private readonly WebhookHashAlgorithm _algorithm;
    private readonly WebhookSignatureEncoding _encoding;
    private readonly WebhookSignatureInput _input;
    private readonly string _timestampSeparator;

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

    public WebhookSignatureGenerator(WebhookSignatureOptions options)
        : this(GetPrimarySecret(options), options ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    public WebhookHashAlgorithm Algorithm => _algorithm;
    public WebhookSignatureEncoding Encoding => _encoding;
    public WebhookSignatureInput Input => _input;
    public WebhookSignatureInput SigningInput => _input;
    public string TimestampSeparator => _timestampSeparator;

    public string Generate(byte[] rawBody, string? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        return Generate(rawBody.AsSpan(), timestamp);
    }

    public string Generate(ReadOnlySpan<byte> rawBody, string? timestamp = null)
    {
        return GenerateForInput(rawBody, timestamp, _input, _timestampSeparator);
    }

    public string Generate(
        byte[] rawBody,
        string timestamp,
        WebhookSignatureInput input,
        string? timestampSeparator = null)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        return GenerateForInput(rawBody.AsSpan(), timestamp, input, timestampSeparator ?? DefaultTimestampSeparator);
    }

    public string Generate(
        ReadOnlySpan<byte> rawBody,
        string timestamp,
        WebhookSignatureInput input,
        string? timestampSeparator = null)
    {
        return GenerateForInput(rawBody, timestamp, input, timestampSeparator ?? DefaultTimestampSeparator);
    }

    public string GenerateCustom(byte[] signingInput)
    {
        ArgumentNullException.ThrowIfNull(signingInput);
        return GenerateCustom(signingInput.AsSpan());
    }

    public string GenerateCustom(ReadOnlySpan<byte> signingInput)
    {
        return Encode(ComputeHash(_primarySecret, signingInput));
    }

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

    public string GenerateWithCustomInput(
        ReadOnlySpan<byte> rawBody,
        string? timestamp,
        Func<ReadOnlyMemory<byte>, string?, ReadOnlyMemory<byte>> customSigningInput)
    {
        ArgumentNullException.ThrowIfNull(customSigningInput);
        var input = customSigningInput(rawBody.ToArray(), timestamp);
        return GenerateCustom(input.Span);
    }

    public string GenerateFromSigningInput(byte[] signingInput)
    {
        return GenerateCustom(signingInput);
    }

    public bool Verify(string signature, byte[] rawBody, string? timestamp = null)
    {
        return Verify(signature, rawBody.AsSpan(), timestamp);
    }

    public bool Verify(string signature, ReadOnlySpan<byte> rawBody, string? timestamp = null)
    {
        return VerifyResult(signature, rawBody, timestamp).IsValid;
    }

    public bool Verify(byte[] rawBody, string signature, string? timestamp = null)
    {
        return Verify(signature, rawBody, timestamp);
    }

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

    public bool Verify(
        string signature,
        ReadOnlySpan<byte> rawBody,
        string timestamp,
        WebhookSignatureInput input,
        string? timestampSeparator = null)
    {
        return VerifyResult(signature, rawBody, timestamp, input, timestampSeparator).IsValid;
    }

    public bool VerifyCustom(string signature, byte[] signingInput)
    {
        ArgumentNullException.ThrowIfNull(signingInput);
        return VerifyCustom(signature, signingInput.AsSpan());
    }

    public bool VerifyCustom(string signature, ReadOnlySpan<byte> signingInput)
    {
        return VerifyBytes(signature, signingInput).IsValid;
    }

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

    public WebhookVerificationResult VerifyResult(
        string signature,
        byte[] rawBody,
        string? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        return VerifyResult(signature, rawBody.AsSpan(), timestamp, cancellationToken);
    }

    public WebhookVerificationResult VerifyResult(
        string signature,
        ReadOnlySpan<byte> rawBody,
        string? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        return VerifyResult(signature, rawBody, timestamp, _input, _timestampSeparator, cancellationToken);
    }

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

    public WebhookVerificationResult VerifyCustomResult(
        string signature,
        byte[] signingInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signingInput);
        return VerifyCustomResult(signature, signingInput.AsSpan(), cancellationToken);
    }

    public WebhookVerificationResult VerifyCustomResult(
        string signature,
        ReadOnlySpan<byte> signingInput,
        CancellationToken cancellationToken = default)
    {
        return VerifyBytes(signature, signingInput, cancellationToken);
    }

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
