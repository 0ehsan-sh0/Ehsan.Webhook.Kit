namespace WebhookKit.Abstractions.Exceptions;

public sealed class WebhookRetryableException : Exception
{
    public WebhookRetryableException()
        : base("The webhook operation is retryable.")
    {
    }

    public WebhookRetryableException(string message)
        : base(message)
    {
    }

    public WebhookRetryableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
