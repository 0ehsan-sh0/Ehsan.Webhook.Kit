// Copyright (c) Ehsan. Licensed under the MIT License.
using System.Buffers;
using Microsoft.AspNetCore.Http;
using WebhookKit.AspNetCore.Exceptions;

namespace WebhookKit.AspNetCore;

/// <summary>
/// Default implementation of <see cref="IWebhookBodyReader"/> that buffers the request stream,
/// enforces size limits against DoS attacks, and rewinds the stream for downstream consumers.
/// </summary>
public sealed class WebhookBodyReader : IWebhookBodyReader
{
    private const int BufferSize = 81920; // 80 KB chunk buffer

    public async ValueTask<byte[]> ReadRawBodyAsync(
        HttpContext context,
        long maxSizeBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (maxSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSizeBytes), "Max request body size must be non-negative.");
        }

        var request = context.Request;

        // 1. Fast-path check: Content-Length header
        if (request.ContentLength.HasValue && request.ContentLength.Value > maxSizeBytes)
        {
            throw new WebhookPayloadTooLargeException(request.ContentLength.Value, maxSizeBytes);
        }

        // 2. Enable buffering so the stream can be rewound and read downstream
        request.EnableBuffering();

        var bodyStream = request.Body;
        if (bodyStream.CanSeek && bodyStream.Position != 0)
        {
            bodyStream.Position = 0;
        }

        // 3. Read stream in chunks, verifying accumulated size
        var initialBufferSize = maxSizeBytes < BufferSize ? (int)maxSizeBytes + 1 : BufferSize;
        byte[] rentBuffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);
        using var memoryStream = new MemoryStream(request.ContentLength.HasValue ? (int)Math.Min(request.ContentLength.Value, int.MaxValue) : 0);
        try
        {
            long totalBytesRead = 0;

            while (true)
            {
                var remainingBytes = maxSizeBytes - totalBytesRead;
                var readLength = remainingBytes < rentBuffer.Length ? (int)remainingBytes + 1 : rentBuffer.Length;
                var bytesRead = await bodyStream.ReadAsync(rentBuffer.AsMemory(0, readLength), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                totalBytesRead += bytesRead;
                if (totalBytesRead > maxSizeBytes)
                {
                    throw new WebhookPayloadTooLargeException(totalBytesRead, maxSizeBytes);
                }

                memoryStream.Write(rentBuffer, 0, bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentBuffer);

            // 4. Rewind stream position so downstream middleware / model binding can read it
            if (bodyStream.CanSeek)
            {
                bodyStream.Position = 0;
            }
        }

        return memoryStream.Length == 0 ? [] : memoryStream.ToArray();
    }
}
