using WebhookKit.Core.Options;

namespace WebhookKit.Core.Handlers;

public sealed class WebhookHandlerRegistry
{
    private static readonly IReadOnlyList<WebhookHandlerDescriptor> Empty = Array.Empty<WebhookHandlerDescriptor>();
    private readonly Dictionary<string, IReadOnlyList<WebhookHandlerDescriptor>> _descriptors;

    public WebhookHandlerRegistry(IEnumerable<WebhookHandlerDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var grouped = new Dictionary<string, List<WebhookHandlerDescriptor>>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            if (descriptor is null)
            {
                throw new WebhookConfigurationException("Webhook handler descriptors cannot contain null values.");
            }

            if (!grouped.TryGetValue(descriptor.EventType, out var eventDescriptors))
            {
                eventDescriptors = new List<WebhookHandlerDescriptor>();
                grouped.Add(descriptor.EventType, eventDescriptors);
            }

            eventDescriptors.Add(descriptor);
        }

        var snapshot = new Dictionary<string, IReadOnlyList<WebhookHandlerDescriptor>>(StringComparer.Ordinal);
        foreach (var (eventType, eventDescriptors) in grouped)
        {
            snapshot.Add(eventType, Array.AsReadOnly(eventDescriptors.ToArray()));
        }

        _descriptors = snapshot;
    }

    public IReadOnlyList<WebhookHandlerDescriptor> GetHandlers(string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return _descriptors.TryGetValue(eventType, out var descriptors) ? descriptors : Empty;
    }
}
