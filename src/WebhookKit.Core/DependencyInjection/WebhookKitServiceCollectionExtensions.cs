// Copyright (c) Ehsan. Licensed under the MIT License.
using Microsoft.Extensions.DependencyInjection;
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

        return services;
    }
}
