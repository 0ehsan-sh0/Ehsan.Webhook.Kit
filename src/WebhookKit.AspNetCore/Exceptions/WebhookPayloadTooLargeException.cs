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

    /// <summary>Creates an exception with observed and configured body sizes.</summary>
    /// <param name="actualBytes">Actual or advertised body size in bytes.</param>
    /// <param name="maxBytes">Effective maximum body size in bytes.</param>
    public WebhookPayloadTooLargeException(long actualBytes, long maxBytes)
        : base($"Webhook request body size ({actualBytes} bytes) exceeded the configured maximum allowed size ({maxBytes} bytes).")
    {
        ActualBytes = actualBytes;
        MaxBytes = maxBytes;
    }

    /// <summary>Creates an exception with a safe caller-supplied message.</summary>
    /// <param name="message">A message that must not contain raw payload data.</param>
    public WebhookPayloadTooLargeException(string message) : base(message)
    {
    }

    /// <summary>Creates an exception with a safe message and inner error.</summary>
    /// <param name="message">A message that must not contain raw payload data.</param>
    /// <param name="innerException">The original error, when available.</param>
    public WebhookPayloadTooLargeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
