// Copyright (c) Ehsan. Licensed under the MIT License.
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore.Mvc;
using WebhookKit.AspNetCore.Pipeline;
using WebhookKit.AspNetCore.Responses;

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

    /// <summary>Registers body reading, response formatting, endpoint processing, and MVC integration.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for fluent registration.</returns>
    public static IServiceCollection AddWebhookKitAspNetCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddWebhookBodyReader();
        services.TryAddSingleton<IWebhookResponseFormatter, DefaultWebhookResponseFormatter>();
        services.TryAddSingleton<WebhookResponseWriter>();
        services.TryAddScoped<WebhookEndpointService>();
        services.TryAddScoped<IWebhookEndpointService>(sp => sp.GetRequiredService<WebhookEndpointService>());
        services.TryAddScoped<WebhookEndpointFilter>();
        services.Configure<MvcOptions>(options =>
        {
            options.Filters.Add<WebhookEndpointFilter>();
            options.ModelBinderProviders.Insert(0, new WebhookContextModelBinderProvider());
        });
        return services;
    }

    /// <summary>Compatibility alias for <see cref="AddWebhookKitAspNetCore"/>.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for fluent registration.</returns>
    public static IServiceCollection AddWebhookAspNetCore(this IServiceCollection services)
    {
        return services.AddWebhookKitAspNetCore();
    }
}
