// Copyright (c) Ehsan. Licensed under the MIT License.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebhookKit.AspNetCore.Pipeline;

namespace WebhookKit.AspNetCore.DependencyInjection;

/// <summary>
/// Extension methods for setting up WebhookKit ASP.NET Core services.
/// </summary>
public static class WebhookKitAspNetCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IWebhookBodyReader"/> service.
    /// </summary>
    public static IServiceCollection AddWebhookBodyReader(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IWebhookBodyReader, WebhookBodyReader>();
        return services;
    }

    public static IServiceCollection AddWebhookKitAspNetCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddWebhookBodyReader();
        services.TryAddScoped<WebhookEndpointService>();
        services.TryAddScoped<IWebhookEndpointService>(sp => sp.GetRequiredService<WebhookEndpointService>());
        return services;
    }

    public static IServiceCollection AddWebhookAspNetCore(this IServiceCollection services)
    {
        return services.AddWebhookKitAspNetCore();
    }
}
