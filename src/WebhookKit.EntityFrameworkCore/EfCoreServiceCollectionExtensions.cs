using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebhookKit.Abstractions;

namespace WebhookKit.EntityFrameworkCore;

public static class EfCoreServiceCollectionExtensions
{
    public static IServiceCollection AddWebhookKitEntityFrameworkCore<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IWebhookUniqueConstraintDetector, ProviderNeutralWebhookUniqueConstraintDetector>();
        services.AddScoped<IWebhookStore, EfCoreWebhookStore<TContext>>();
        return services;
    }
}
