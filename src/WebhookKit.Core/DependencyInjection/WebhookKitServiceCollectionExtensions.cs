// Copyright (c) Ehsan. Licensed under the MIT License.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Core.Clocks;
using WebhookKit.Core.Handlers;
using WebhookKit.Core.Options;
using WebhookKit.Core.Processing;

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
                options.JsonSerializerOptions = prebuilt.JsonSerializerOptions;
                foreach (var (name, provider) in prebuilt.Providers)
                {
                    options.Providers[name] = provider;
                }
            })
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<WebhookKitOptions>, WebhookKitOptionsValidator>();
        services.AddSingleton<IWebhookClock, SystemWebhookClock>();
        services.TryAddSingleton<IWebhookDeserializer, Deserialization.SystemTextJsonWebhookDeserializer>();
        services.TryAddSingleton<IWebhookStore, Stores.InMemoryWebhookStore>();
        services.TryAddSingleton<Deduplication.WebhookDeduplicationKeyFactory>();
        services.TryAddSingleton<Deduplication.DefaultWebhookDeduplicator>();
        services.TryAddSingleton<IWebhookDeduplicator>(sp => sp.GetRequiredService<Deduplication.DefaultWebhookDeduplicator>());

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

        AddProcessingServices(services);
        return services;
    }

    public static IServiceCollection AddWebhookHandler<THandler>(this IServiceCollection services, string eventType)
        where THandler : class
    {
        return AddWebhookHandler(services, typeof(THandler), eventType);
    }

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
        services.TryAddScoped<IWebhookProcessor>(sp => sp.GetRequiredService<WebhookProcessor>());
        services.TryAddScoped<IWebhookDispatchProcessor>(sp => sp.GetRequiredService<WebhookProcessor>());
        services.TryAddScoped<WebhookIngestionService>();
    }
}
