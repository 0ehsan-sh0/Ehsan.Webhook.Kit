namespace WebhookKit.Abstractions.Exceptions;

/// <summary>Indicates that a verified webhook body could not produce a valid typed payload.</summary>
public sealed class WebhookPayloadException : Exception
{
    /// <summary>Creates a payload exception with a safe default message.</summary>
    public WebhookPayloadException()
        : base("The webhook payload could not be deserialized.")
    {
    }
}
