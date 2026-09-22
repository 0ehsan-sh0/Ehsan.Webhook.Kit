// Copyright (c) Ehsan. Licensed under the MIT License.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Core.Clocks;
using WebhookKit.Core.Options;

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
                foreach (var (name, provider) in prebuilt.Providers)
                {
                    options.Providers[name] = provider;
                }
            })
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<WebhookKitOptions>, WebhookKitOptionsValidator>();
        services.AddSingleton<IWebhookClock, SystemWebhookClock>();

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

        return services;
    }
}
