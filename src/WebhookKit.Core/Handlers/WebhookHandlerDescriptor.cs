using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using WebhookKit.Abstractions;
using WebhookKit.Core.Options;

namespace WebhookKit.Core.Handlers;

/// <summary>Describes a registered strongly typed handler and its event mapping.</summary>
internal sealed class WebhookHandlerDescriptor
{
    private readonly IWebhookHandlerInvoker _invoker;

    private WebhookHandlerDescriptor(
        string eventType,
        Type implementationType,
        Type payloadType,
        Type handlerInterfaceType,
        IWebhookHandlerInvoker invoker)
    {
        EventType = eventType;
        ImplementationType = implementationType;
        PayloadType = payloadType;
        HandlerInterfaceType = handlerInterfaceType;
        _invoker = invoker;
    }

    /// <summary>Provider event type routed to this handler.</summary>
    public string EventType { get; }

    /// <summary>Concrete handler implementation resolved from scoped DI.</summary>
    public Type ImplementationType { get; }

    /// <summary>Closed payload type accepted by the handler.</summary>
    public Type PayloadType { get; }

    /// <summary>Closed <see cref="IWebhookHandler{T}"/> contract implemented by the handler.</summary>
    public Type HandlerInterfaceType { get; }

    internal static WebhookHandlerDescriptor Create(Type implementationType, string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new WebhookConfigurationException("Webhook handler event type must be non-empty.");
        }

        if (implementationType is null ||
            !implementationType.IsClass ||
            implementationType.IsAbstract ||
            implementationType.ContainsGenericParameters)
        {
            throw new WebhookConfigurationException("Webhook handler must be a concrete, closed implementation type.");
        }

        var handlerContracts = implementationType
            .GetInterfaces()
            .Where(IsHandlerContract)
            .ToArray();

        if (handlerContracts.Length != 1)
        {
            throw new WebhookConfigurationException("Webhook handler must implement exactly one IWebhookHandler<T> contract.");
        }

        var handlerInterfaceType = handlerContracts[0];
        var payloadType = handlerInterfaceType.GetGenericArguments()[0];
        if (payloadType.ContainsGenericParameters)
        {
            throw new WebhookConfigurationException("Webhook handler payload type must be closed.");
        }

        if (implementationType.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Length == 0)
        {
            throw new WebhookConfigurationException("Webhook handler must expose a public constructor.");
        }

        var invokerType = typeof(TypedInvoker<,>).MakeGenericType(implementationType, payloadType);
        var invoker = (IWebhookHandlerInvoker)Activator.CreateInstance(invokerType)!;
        return new WebhookHandlerDescriptor(eventType, implementationType, payloadType, handlerInterfaceType, invoker);
    }

    internal Task InvokeAsync(IServiceProvider serviceProvider, WebhookContext context, CancellationToken cancellationToken)
    {
        return _invoker.InvokeAsync(serviceProvider, context, cancellationToken);
    }

    private static bool IsHandlerContract(Type type)
    {
        return type.IsGenericType &&
               type.GetGenericTypeDefinition() == typeof(IWebhookHandler<>);
    }

    private interface IWebhookHandlerInvoker
    {
        Task InvokeAsync(IServiceProvider serviceProvider, WebhookContext context, CancellationToken cancellationToken);
    }

    private sealed class TypedInvoker<TImplementation, TPayload> : IWebhookHandlerInvoker
        where TImplementation : IWebhookHandler<TPayload>
    {
        public async Task InvokeAsync(IServiceProvider serviceProvider, WebhookContext context, CancellationToken cancellationToken)
        {
            var handler = (TImplementation)serviceProvider.GetRequiredService(typeof(TImplementation));
            await handler.HandleAsync(context.GetPayload<TPayload>(), context, cancellationToken).ConfigureAwait(false);
        }
    }
}
