namespace WebhookKit.Abstractions.Exceptions;

public sealed class WebhookPayloadException : Exception
{
    public WebhookPayloadException()
        : base("The webhook payload could not be deserialized.")
    {
    }
}
