// Copyright (c) Ehsan. Licensed under the MIT License.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Core.Clocks;
using WebhookKit.Core.Handlers;
using WebhookKit.Core.Options;
using WebhookKit.Core.Processing;
using WebhookKit.Core.Retries;

namespace WebhookKit.Core.DependencyInjection;

/// <summary>DI registration for WebhookKit foundations.</summary>
public static class WebhookKitServiceCollectionExtensions
{
    /// <summary>
    /// Register WebhookKit options, validation, and the system clock.
    /// Invalid configuration fails fast at startup via <c>ValidateOnStart</c>.
    /// </summary>
    public static IServiceCollection AddWebhookKit(this IServiceCollection services, Action<WebhookKitOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var prebuilt = new WebhookKitOptions();
        configure?.Invoke(prebuilt);

        services.AddOptions<WebhookKitOptions>()
            .Configure(options =>
            {
                options.MaxRequestBodySizeBytes = prebuilt.MaxRequestBodySizeBytes;
                options.Storage = prebuilt.Storage;
                options.Queue = prebuilt.Queue;
                options.Background = prebuilt.Background;
                options.JsonSerializerOptions = prebuilt.JsonSerializerOptions;
                foreach (var (name, provider) in prebuilt.Providers)
                {
                    options.Providers[name] = provider;
                }
            })
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<WebhookKitOptions>, WebhookKitOptionsValidator>();
        services.TryAddSingleton<IWebhookClock, SystemWebhookClock>();
        services.TryAddSingleton<IWebhookIdGenerator, Clocks.WebhookIdGenerator>();
        services.TryAddSingleton<IWebhookDeserializer, Deserialization.SystemTextJsonWebhookDeserializer>();
        services.TryAddSingleton<IWebhookStore, Stores.InMemoryWebhookStore>();
        services.TryAddSingleton<Queues.ChannelWebhookQueue>();
        services.TryAddSingleton<IWebhookQueue>(sp => sp.GetRequiredService<Queues.ChannelWebhookQueue>());
        services.TryAddScoped<Deduplication.DefaultWebhookDeduplicator>();
        services.TryAddScoped<IWebhookDeduplicator>(sp => sp.GetRequiredService<Deduplication.DefaultWebhookDeduplicator>());

        // Verifiers
        services.TryAddSingleton<IWebhookSignatureVerifier, Verifiers.HmacSignatureVerifier>();
        services.TryAddSingleton<IWebhookTimestampVerifier, Verifiers.WebhookTimestampVerifier>();

        // Extractors
        services.TryAddSingleton<Extractors.HeaderEventIdExtractor>();
        services.TryAddSingleton<Extractors.JsonEventIdExtractor>();
        services.TryAddSingleton<IWebhookEventIdExtractor>(sp => new Extractors.CompositeWebhookEventIdExtractor(
        [
            sp.GetRequiredService<Extractors.HeaderEventIdExtractor>(),
            sp.GetRequiredService<Extractors.JsonEventIdExtractor>()
        ]));

        services.TryAddSingleton<Extractors.HeaderEventTypeExtractor>();
        services.TryAddSingleton<Extractors.JsonEventTypeExtractor>();
        services.TryAddSingleton<IWebhookEventTypeExtractor>(sp => new Extractors.CompositeWebhookEventTypeExtractor(
        [
            sp.GetRequiredService<Extractors.HeaderEventTypeExtractor>(),
            sp.GetRequiredService<Extractors.JsonEventTypeExtractor>()
        ]));

        services.TryAddSingleton<WebhookRetryPolicy>();
        services.TryAddSingleton<IWebhookRetryDelay, TaskWebhookRetryDelay>();
        services.TryAddSingleton<IWebhookRetryExecutor, WebhookRetryExecutor>();

        AddProcessingServices(services);
        if (prebuilt.Background is { Enabled: true })
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, Workers.WebhookBackgroundWorker>());
        }

        return services;
    }

    /// <summary>Registers a strongly typed handler for an event type.</summary>
    /// <typeparam name="THandler">Concrete handler type resolved from scoped DI.</typeparam>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="eventType">Provider event type routed to the handler.</param>
    /// <returns>The same service collection for fluent registration.</returns>
    public static IServiceCollection AddWebhookHandler<THandler>(this IServiceCollection services, string eventType)
        where THandler : class
    {
        return AddWebhookHandler(services, typeof(THandler), eventType);
    }

    /// <summary>Registers a strongly typed handler type for an event type.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="implementationType">Concrete closed handler type.</param>
    /// <param name="eventType">Provider event type routed to the handler.</param>
    /// <returns>The same service collection for fluent registration.</returns>
    public static IServiceCollection AddWebhookHandler(
        this IServiceCollection services,
        Type implementationType,
        string eventType)
    {
        ArgumentNullException.ThrowIfNull(services);

        var descriptor = WebhookHandlerDescriptor.Create(implementationType, eventType);
        services.Add(new ServiceDescriptor(implementationType, implementationType, ServiceLifetime.Scoped));
        services.AddSingleton(descriptor);
        AddProcessingServices(services);
        return services;
    }

    private static void AddProcessingServices(IServiceCollection services)
    {
        services.TryAddSingleton<WebhookHandlerRegistry>();
        services.TryAddScoped<WebhookProcessor>();
        services.TryAddScoped<IWebhookDispatchProcessor>(sp => sp.GetRequiredService<WebhookProcessor>());
        services.TryAddScoped<WebhookIngestionService>();
    }
}
