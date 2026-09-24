using Microsoft.AspNetCore.Mvc.Filters;
using WebhookKit.AspNetCore.Pipeline;

namespace WebhookKit.AspNetCore.Mvc;

[Flags]
public enum WebhookEndpointActionPolicy
{
    None = 0,
    Processed = 1 << 0,
    Accepted = 1 << 1,
    Duplicate = 1 << 2,
    Ignored = 1 << 3,
    Handled = Accepted | Duplicate | Ignored,
    All = Processed | Handled
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class WebhookEndpointAttribute : Attribute, IFilterMetadata
{
    public WebhookEndpointAttribute(string providerName)
    {
        ProviderName = providerName;
    }

    public string ProviderName { get; set; }

    public string Provider
    {
        get => ProviderName;
        set => ProviderName = value;
    }

    public WebhookProcessingMode Mode { get; set; } = WebhookProcessingMode.Synchronous;

    public WebhookProcessingMode ProcessingMode
    {
        get => Mode;
        set => Mode = value;
    }

    public WebhookEndpointActionPolicy ActionPolicy { get; set; } = WebhookEndpointActionPolicy.Processed;

    internal WebhookEndpointOptions CreateOptions()
    {
        var options = new WebhookEndpointOptions
        {
            ProviderName = ProviderName,
            Mode = Mode
        };
        options.Validate();
        return options.Snapshot();
    }
}
