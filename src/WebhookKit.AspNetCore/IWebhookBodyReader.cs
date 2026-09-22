// Copyright (c) Ehsan. Licensed under the MIT License.
using Microsoft.AspNetCore.Http;

namespace WebhookKit.AspNetCore;

/// <summary>
/// Captures and buffers the exact raw byte stream of an incoming HTTP request before any framework stream reading or deserialization occurs.
/// </summary>
public interface IWebhookBodyReader
{
    /// <summary>
    /// Reads the raw request body into a byte array, enforcing strict size limits and buffering the stream for downstream consumers.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="maxSizeBytes">The maximum allowed body size in bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw body bytes.</returns>
    /// <exception cref="Exceptions.WebhookPayloadTooLargeException">Thrown when the body exceeds <paramref name="maxSizeBytes"/>.</exception>
    ValueTask<byte[]> ReadRawBodyAsync(HttpContext context, long maxSizeBytes, CancellationToken cancellationToken = default);
}
