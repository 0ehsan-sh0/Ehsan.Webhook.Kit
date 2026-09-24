using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebhookKit.Abstractions;

namespace WebhookKit.EntityFrameworkCore;

/// <summary>Registers Entity Framework Core webhook persistence.</summary>
public static class EfCoreServiceCollectionExtensions
{
    /// <summary>Registers the EF Core webhook store for an application context.</summary>
    /// <typeparam name="TContext">Application DbContext type.</typeparam>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for fluent registration.</returns>
    public static IServiceCollection AddWebhookKitEntityFrameworkCore<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IWebhookUniqueConstraintDetector, ProviderNeutralWebhookUniqueConstraintDetector>();
        services.AddScoped<IWebhookStore, EfCoreWebhookStore<TContext>>();
        return services;
    }
}
