namespace WebhookKit.Abstractions.Exceptions;

public sealed class WebhookPermanentException : Exception
{
    public WebhookPermanentException()
        : base("The webhook operation is permanent.")
    {
    }

    public WebhookPermanentException(string message)
        : base(message)
    {
    }

    public WebhookPermanentException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
