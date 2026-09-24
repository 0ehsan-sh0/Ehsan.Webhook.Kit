using Microsoft.AspNetCore.Mvc.Filters;
using WebhookKit.AspNetCore.Pipeline;

namespace WebhookKit.AspNetCore.Mvc;

/// <summary>Selects which successful endpoint outcomes continue to an MVC action.</summary>
[Flags]
public enum WebhookEndpointActionPolicy
{
    /// <summary>Do not continue to the action for any outcome.</summary>
    None = 0,
    /// <summary>Continue for a synchronously processed delivery.</summary>
    Processed = 1 << 0,
    /// <summary>Continue for an asynchronously accepted delivery.</summary>
    Accepted = 1 << 1,
    /// <summary>Continue for a duplicate delivery.</summary>
    Duplicate = 1 << 2,
    /// <summary>Continue for an ignored delivery.</summary>
    Ignored = 1 << 3,
    /// <summary>Continue for accepted, duplicate, and ignored outcomes.</summary>
    Handled = Accepted | Duplicate | Ignored,
    /// <summary>Continue for all supported successful outcomes.</summary>
    All = Processed | Handled
}

/// <summary>Marks an MVC action or controller as a WebhookKit endpoint.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class WebhookEndpointAttribute : Attribute, IFilterMetadata
{
    /// <summary>Creates an endpoint attribute for a provider.</summary>
    /// <param name="providerName">Configured provider name.</param>
    public WebhookEndpointAttribute(string providerName)
    {
        ProviderName = providerName;
    }

    /// <summary>Configured provider name.</summary>
    public string ProviderName { get; set; }

    /// <summary>Compatibility alias for <see cref="ProviderName"/>.</summary>
    public string Provider
    {
        get => ProviderName;
        set => ProviderName = value;
    }

    /// <summary>Synchronous or asynchronous processing mode.</summary>
    public WebhookProcessingMode Mode { get; set; } = WebhookProcessingMode.Synchronous;

    /// <summary>Compatibility alias for <see cref="Mode"/>.</summary>
    public WebhookProcessingMode ProcessingMode
    {
        get => Mode;
        set => Mode = value;
    }

    /// <summary>Successful outcomes for which the MVC action is invoked.</summary>
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
