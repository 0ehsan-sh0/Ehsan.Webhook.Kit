using System.Text.Json;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Abstractions.Exceptions;
using WebhookKit.Core.Options;

namespace WebhookKit.Core.Deserialization;

/// <summary>Deserializes webhook bodies with the configured System.Text.Json options.</summary>
internal sealed class SystemTextJsonWebhookDeserializer : IWebhookDeserializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>Creates a deserializer using the current WebhookKit JSON options.</summary>
    /// <param name="options">WebhookKit options containing the serializer settings.</param>
    public SystemTextJsonWebhookDeserializer(IOptions<WebhookKitOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.JsonSerializerOptions;
    }

    /// <summary>Deserializes an exact request body into <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The application payload type.</typeparam>
    /// <param name="rawBody">The exact request bytes; they are not modified or retained.</param>
    /// <param name="cancellationToken">Token used to cancel deserialization.</param>
    /// <returns>The non-null payload.</returns>
    /// <exception cref="WebhookPayloadException">The JSON is invalid or produces <see langword="null"/>.</exception>
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
