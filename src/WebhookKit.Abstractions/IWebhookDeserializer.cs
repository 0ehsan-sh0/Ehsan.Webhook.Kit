namespace WebhookKit.Abstractions;

/// <summary>Deserializes the exact request bytes into an application event type.</summary>
public interface IWebhookDeserializer
{
    /// <summary>Deserializes <paramref name="rawBody"/> into <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The application payload type.</typeparam>
    /// <param name="rawBody">The unmodified request bytes; the implementation must not expose or retain secret material.</param>
    /// <param name="cancellationToken">Token used to stop deserialization.</param>
    /// <returns>The non-null deserialized payload.</returns>
    /// <exception cref="Exceptions.WebhookPayloadException">The payload is empty, malformed, or deserializes to <see langword="null"/>.</exception>
    T Deserialize<T>(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default);
}
