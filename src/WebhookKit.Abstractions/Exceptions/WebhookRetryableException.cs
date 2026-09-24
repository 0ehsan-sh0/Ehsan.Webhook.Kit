namespace WebhookKit.Abstractions.Exceptions;

/// <summary>Indicates a transient webhook processing failure eligible for configured retries.</summary>
public sealed class WebhookRetryableException : Exception
{
    /// <summary>Creates a retryable failure with a safe default message.</summary>
    public WebhookRetryableException()
        : base("The webhook operation is retryable.")
    {
    }

    /// <summary>Creates a retryable failure with a caller-supplied safe message.</summary>
    /// <param name="message">A non-sensitive failure message.</param>
    public WebhookRetryableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a retryable failure with an inner exception.</summary>
    /// <param name="message">A non-sensitive failure message.</param>
    /// <param name="innerException">The original failure, when available.</param>
    public WebhookRetryableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
