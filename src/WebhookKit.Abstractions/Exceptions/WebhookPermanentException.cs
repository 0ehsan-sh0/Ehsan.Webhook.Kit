namespace WebhookKit.Abstractions.Exceptions;

/// <summary>Indicates a non-retryable webhook processing failure.</summary>
public sealed class WebhookPermanentException : Exception
{
    /// <summary>Creates a permanent failure with a safe default message.</summary>
    public WebhookPermanentException()
        : base("The webhook operation is permanent.")
    {
    }

    /// <summary>Creates a permanent failure with a caller-supplied safe message.</summary>
    /// <param name="message">A non-sensitive failure message.</param>
    public WebhookPermanentException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a permanent failure with an inner exception.</summary>
    /// <param name="message">A non-sensitive failure message.</param>
    /// <param name="innerException">The original failure, when available.</param>
    public WebhookPermanentException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
