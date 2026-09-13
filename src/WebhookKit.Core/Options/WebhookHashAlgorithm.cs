// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Core.Options;

/// <summary>Supported HMAC algorithms for signature verification.</summary>
public enum WebhookHashAlgorithm
{
    /// <summary>HMAC-SHA256.</summary>
    HmacSha256 = 0,

    /// <summary>HMAC-SHA512.</summary>
    HmacSha512 = 1,
}
