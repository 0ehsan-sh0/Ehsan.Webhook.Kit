// Copyright (c) Ehsan. Licensed under the MIT License.
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using WebhookKit.Abstractions.Exceptions;

namespace WebhookKit.Abstractions;

/// <summary>
/// Foundations slice of the execution context passed to application handlers.
/// Shields secrets and raw buffers; exposes extracted metadata only.
/// Payload helpers are lazy and keep the raw buffer private.
/// </summary>
public sealed class WebhookContext
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyHeaders =
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

    private readonly ConcurrentDictionary<Type, Lazy<object>> _payloadCache = new();
    private readonly byte[]? _rawBody;
    private readonly IWebhookDeserializer? _deserializer;
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _headers = EmptyHeaders;
    private string? _correlationId;

    /// <summary>Creates an empty context for metadata-only scenarios.</summary>
    public WebhookContext()
    {
    }

    /// <summary>Creates a context that can lazily deserialize a copied request body.</summary>
    /// <param name="rawBody">The exact request bytes; the constructor takes a private copy.</param>
    /// <param name="deserializer">The deserializer used by the <c>GetPayload</c> helper.</param>
    public WebhookContext(ReadOnlyMemory<byte> rawBody, IWebhookDeserializer? deserializer)
    {
        _deserializer = deserializer ?? throw new WebhookPayloadException();
        _rawBody = rawBody.ToArray();
    }

    /// <summary>WebhookKit-generated transmission identifier.</summary>
    public required string WebhookId { get; init; }

    /// <summary>Application correlation identifier, or a generated value when none was supplied.</summary>
    public string CorrelationId
    {
        get
        {
            var correlationId = _correlationId;
            if (correlationId is not null)
            {
                return correlationId;
            }

            var generated = CreateCorrelationId();
            return Interlocked.CompareExchange(ref _correlationId, generated, null) ?? generated;
        }
        init => _correlationId = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Configured provider name.</summary>
    public required string Provider { get; init; }

    /// <summary>Provider event identifier, if extracted.</summary>
    public string? EventId { get; init; }

    /// <summary>Provider event type, if extracted.</summary>
    public string? EventType { get; init; }

    /// <summary>Instant the request was received.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Provider-supplied event timestamp, if extracted.</summary>
    public DateTimeOffset? ProviderTimestamp { get; init; }

    /// <summary>Read-only request header snapshot with multiple values per key.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Headers
    {
        get => _headers;
        init => _headers = WebhookHeaderSnapshot.Create(value);
    }

    /// <summary>Gets the typed payload, deserializing and caching it on first use.</summary>
    /// <typeparam name="T">The application event payload type.</typeparam>
    /// <returns>The non-null deserialized payload.</returns>
    /// <exception cref="WebhookPayloadException">The context has no body/deserializer or the payload is invalid.</exception>
    public T GetPayload<T>()
    {
        return GetPayload<T>(typeof(T));
    }

    /// <summary>Returns non-sensitive context metadata for diagnostics.</summary>
    /// <returns>A metadata-only string that never includes the raw body or secrets.</returns>
    public override string ToString()
    {
        return $"WebhookContext {{ WebhookId = {WebhookId}, Provider = {Provider}, EventId = {EventId}, EventType = {EventType}, ReceivedAt = {ReceivedAt:O} }}";
    }

    private T GetPayload<T>(Type? payloadType)
    {
        if (_rawBody is null || _deserializer is null || payloadType is null || payloadType.ContainsGenericParameters)
        {
            throw new WebhookPayloadException();
        }

        var payload = _payloadCache.GetOrAdd(
            payloadType,
            type => new Lazy<object>(() => DeserializePayload<T>(type), LazyThreadSafetyMode.ExecutionAndPublication));

        return (T)payload.Value;
    }

    private object DeserializePayload<T>(Type payloadType)
    {
        try
        {
            var payload = _deserializer!.Deserialize<T>(_rawBody);
            if (payload is null)
            {
                throw new WebhookPayloadException();
            }

            return payload;
        }
        catch (WebhookPayloadException)
        {
            _payloadCache.TryRemove(payloadType, out _);
            throw;
        }
        catch
        {
            _payloadCache.TryRemove(payloadType, out _);
            throw new WebhookPayloadException();
        }
    }

    private string CreateCorrelationId()
    {
        string generated;
        do
        {
            generated = Guid.NewGuid().ToString("N");
        }
        while (string.Equals(generated, WebhookId, StringComparison.Ordinal) ||
               string.Equals(generated, EventId, StringComparison.Ordinal));

        return generated;
    }
}

internal static class WebhookHeaderSnapshot
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Create(
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var snapshot = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(header.Key);
            ArgumentNullException.ThrowIfNull(header.Value);
            snapshot[header.Key] = Array.AsReadOnly(header.Value.ToArray());
        }

        return new ReadOnlyDictionary<string, IReadOnlyList<string>>(snapshot);
    }
}
