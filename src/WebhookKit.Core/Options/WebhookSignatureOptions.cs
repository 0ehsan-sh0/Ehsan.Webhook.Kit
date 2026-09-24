// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Core.Options;

/// <summary>Signature verification settings for one provider. Shapes only; engines land in Subgroup 02.</summary>
public sealed class WebhookSignatureOptions
{
    /// <summary>Header carrying the signature. When set, verification is enabled and secrets are required.</summary>
    public string? HeaderName { get; set; }

    /// <summary>Input construction mode for the signature.</summary>
    public WebhookSignatureInput Input { get; set; } = WebhookSignatureInput.RawBody;

    /// <summary>Compatibility alias for <see cref="Input"/>.</summary>
    public WebhookSignatureInput SigningInput
    {
        get => Input;
        set => Input = value;
    }

    /// <summary>Separator placed between a timestamp and the body for timestamp-prefixed signatures.</summary>
    public string TimestampSeparator { get; set; } = ".";

    /// <summary>Compatibility alias for <see cref="TimestampSeparator"/>.</summary>
    public string Separator
    {
        get => TimestampSeparator;
        set => TimestampSeparator = value;
    }

    /// <summary>Hash algorithm. Default HMAC-SHA256.</summary>
    public WebhookHashAlgorithm Algorithm { get; set; } = WebhookHashAlgorithm.HmacSha256;

    /// <summary>Expected signature encoding. Default hex.</summary>
    public WebhookSignatureEncoding Encoding { get; set; } = WebhookSignatureEncoding.Hex;

    /// <summary>Primary shared secret. Prefer configuration providers over source code.</summary>
    public string? Secret { get; set; }

    /// <summary>Additional valid secrets accepted during rotation windows.</summary>
    public List<string> AdditionalSecrets { get; } = [];
}
