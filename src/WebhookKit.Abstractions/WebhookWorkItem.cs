namespace WebhookKit.Abstractions;

public sealed class WebhookWorkItem
{
    private string _webhookId = string.Empty;
    private string _provider = string.Empty;

    public WebhookWorkItem()
    {
    }

    public WebhookWorkItem(string webhookId, string provider)
    {
        WebhookId = webhookId;
        Provider = provider;
    }

    public string WebhookId
    {
        get => _webhookId;
        init
        {
            Validate(value, nameof(WebhookId));
            _webhookId = value;
        }
    }

    public string Provider
    {
        get => _provider;
        init
        {
            Validate(value, nameof(Provider));
            _provider = value;
        }
    }

    private static void Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
    }
}
