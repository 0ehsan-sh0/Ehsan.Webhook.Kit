using WebhookKit.Core.Options;

namespace WebhookKit.Core.Handlers;

/// <summary>Immutable event-type to handler registration snapshot.</summary>
public sealed class WebhookHandlerRegistry
{
    private static readonly IReadOnlyList<WebhookHandlerDescriptor> Empty = Array.Empty<WebhookHandlerDescriptor>();
    private readonly Dictionary<string, IReadOnlyList<WebhookHandlerDescriptor>> _descriptors;

    /// <summary>Creates a registry from a sequence of handler descriptors.</summary>
    /// <param name="descriptors">Descriptors to group by event type; the sequence is snapshotted.</param>
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

    /// <summary>Gets handlers registered for an event type in registration order.</summary>
    /// <param name="eventType">The provider event type.</param>
    /// <returns>A read-only list, empty when no handler is registered.</returns>
    public IReadOnlyList<WebhookHandlerDescriptor> GetHandlers(string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return _descriptors.TryGetValue(eventType, out var descriptors) ? descriptors : Empty;
    }
}
