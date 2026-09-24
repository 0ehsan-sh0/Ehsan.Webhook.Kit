namespace WebhookKit.Core.Options;

public sealed class WebhookStorageOptions
{
    public bool PersistRawBody { get; set; } = true;

    public bool DiscardRawBodyAfterSuccessfulSync { get; set; }
}
