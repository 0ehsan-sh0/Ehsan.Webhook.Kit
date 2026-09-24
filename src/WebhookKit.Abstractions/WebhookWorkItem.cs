namespace WebhookKit.Abstractions;

/// <summary>Identifies one admitted delivery without carrying its raw payload.</summary>
public sealed class WebhookWorkItem
{
    private string _webhookId = string.Empty;
    private string _provider = string.Empty;

    /// <summary>Creates an empty item for object initializers.</summary>
    public WebhookWorkItem()
    {
    }

    /// <summary>Creates a work item for a transmission and provider.</summary>
    /// <param name="webhookId">The WebhookKit transmission identifier.</param>
    /// <param name="provider">The configured provider name.</param>
    public WebhookWorkItem(string webhookId, string provider)
    {
        WebhookId = webhookId;
        Provider = provider;
    }

    /// <summary>WebhookKit transmission identifier.</summary>
    public string WebhookId
    {
        get => _webhookId;
        init
        {
            Validate(value, nameof(WebhookId));
            _webhookId = value;
        }
    }

    /// <summary>Configured provider name associated with the transmission.</summary>
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
