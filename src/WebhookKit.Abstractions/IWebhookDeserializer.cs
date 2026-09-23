namespace WebhookKit.Abstractions;

public interface IWebhookDeserializer
{
    T Deserialize<T>(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default);
}
