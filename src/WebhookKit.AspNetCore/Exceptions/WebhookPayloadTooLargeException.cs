// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.AspNetCore.Exceptions;

/// <summary>
/// Exception thrown when an incoming webhook request body exceeds the configured maximum allowable size.
/// </summary>
public sealed class WebhookPayloadTooLargeException : Exception
{
    /// <summary>The actual or advertised size in bytes that exceeded the limit.</summary>
    public long ActualBytes { get; }

    /// <summary>The maximum configured threshold in bytes.</summary>
    public long MaxBytes { get; }

    public WebhookPayloadTooLargeException(long actualBytes, long maxBytes)
        : base($"Webhook request body size ({actualBytes} bytes) exceeded the configured maximum allowed size ({maxBytes} bytes).")
    {
        ActualBytes = actualBytes;
        MaxBytes = maxBytes;
    }

    public WebhookPayloadTooLargeException(string message) : base(message)
    {
    }

    public WebhookPayloadTooLargeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
