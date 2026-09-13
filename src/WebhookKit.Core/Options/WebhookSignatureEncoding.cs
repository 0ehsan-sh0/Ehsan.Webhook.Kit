// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Core.Options;

/// <summary>Expected encoding of the provider-supplied signature header.</summary>
public enum WebhookSignatureEncoding
{
    /// <summary>Lowercase/uppercase hex.</summary>
    Hex = 0,

    /// <summary>Standard Base64.</summary>
    Base64 = 1,
}
