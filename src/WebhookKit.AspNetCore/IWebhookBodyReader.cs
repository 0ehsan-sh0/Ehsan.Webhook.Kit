// Copyright (c) Ehsan. Licensed under the MIT License.
using Microsoft.AspNetCore.Http;

namespace WebhookKit.AspNetCore;

/// <summary>
/// Captures and buffers the exact raw byte stream of an incoming HTTP request before any framework stream reading or deserialization occurs.
/// </summary>
public interface IWebhookBodyReader
{
    /// <summary>
    /// Reads and buffers the exact request body while enforcing the size limit and rewinding the stream for downstream consumers.
    /// </summary>
    /// <param name="context">The current HTTP request context.</param>
    /// <param name="maxSizeBytes">Maximum accepted body size; must be non-negative.</param>
    /// <param name="cancellationToken">Token used to cancel stream reads.</param>
    /// <returns>A newly owned byte array containing the exact body.</returns>
    /// <exception cref="Exceptions.WebhookPayloadTooLargeException">The declared or observed body exceeds the limit.</exception>
    ValueTask<byte[]> ReadRawBodyAsync(HttpContext context, long maxSizeBytes, CancellationToken cancellationToken = default);
}
