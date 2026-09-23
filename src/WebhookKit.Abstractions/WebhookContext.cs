// Copyright (c) Ehsan. Licensed under the MIT License.
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using WebhookKit.Abstractions.Exceptions;

namespace WebhookKit.Abstractions;

/// <summary>
/// Foundations slice of the execution context passed to application handlers.
/// Shields secrets and raw buffers; exposes extracted metadata only.
/// Extended with payload helpers in Task 12/13.
/// </summary>
public sealed class WebhookContext
{
    private static readonly IReadOnlyDictionary<string, string[]> EmptyHeaders =
        new ReadOnlyDictionary<string, string[]>(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));

    private readonly ConcurrentDictionary<Type, Lazy<object>> _payloadCache = new();
    private readonly byte[]? _rawBody;
    private readonly IWebhookDeserializer? _deserializer;
    private IReadOnlyDictionary<string, string[]> _headers = EmptyHeaders;

    public WebhookContext()
    {
    }

    public WebhookContext(ReadOnlyMemory<byte> rawBody, IWebhookDeserializer? deserializer)
    {
        _deserializer = deserializer ?? throw new WebhookPayloadException();
        _rawBody = rawBody.ToArray();
    }

    /// <summary>WebhookKit-generated transmission identifier.</summary>
    public required string WebhookId { get; init; }

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

    /// <summary>Request headers. Multi-value per key.</summary>
    public required IReadOnlyDictionary<string, string[]> Headers
    {
        get => _headers;
        init => _headers = CreateHeaderSnapshot(value);
    }

    public T GetPayload<T>()
    {
        return GetPayload<T>(typeof(T));
    }

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

    private static ReadOnlyDictionary<string, string[]> CreateHeaderSnapshot(IReadOnlyDictionary<string, string[]> headers)
    {
        var snapshot = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            snapshot[header.Key] = header.Value.ToArray();
        }

        return new ReadOnlyDictionary<string, string[]>(snapshot);
    }
}
