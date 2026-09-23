using System.Text.Json;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Abstractions.Exceptions;
using WebhookKit.Core.Options;

namespace WebhookKit.Core.Deserialization;

public sealed class SystemTextJsonWebhookDeserializer : IWebhookDeserializer
{
    private readonly JsonSerializerOptions _options;

    public SystemTextJsonWebhookDeserializer(IOptions<WebhookKitOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.JsonSerializerOptions;
    }

    public T Deserialize<T>(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return JsonSerializer.Deserialize<T>(rawBody.Span, _options)
                ?? throw new WebhookPayloadException();
        }
        catch (JsonException)
        {
            throw new WebhookPayloadException();
        }
    }
}
