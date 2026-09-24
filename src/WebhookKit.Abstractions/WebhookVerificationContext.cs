// Copyright (c) Ehsan. Licensed under the MIT License.
using System.Collections.ObjectModel;

namespace WebhookKit.Abstractions;

/// <summary>
/// Inputs to signature verification. Carries no secrets.
/// </summary>
public sealed class WebhookVerificationContext
{
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _headers =
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
    /// <summary>Configured provider name.</summary>
    public required string Provider { get; init; }

    /// <summary>Exact raw request bytes covered by the signature; callers must not mutate them during verification.</summary>
    public required byte[] RawBody { get; init; }

    /// <summary>Immutable request headers with multiple read-only values per key.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Headers
    {
        get => _headers;
        init => _headers = WebhookHeaderSnapshot.Create(value);
    }
}
