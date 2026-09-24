using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WebhookKit.Abstractions;

namespace WebhookKit.Redis;

public static class RedisServiceCollectionExtensions
{
    public static IServiceCollection AddWebhookKitRedis(
        this IServiceCollection services,
        IConnectionMultiplexer connectionMultiplexer)
    {
        return AddWebhookKitRedis(services, connectionMultiplexer, null);
    }

    public static IServiceCollection AddWebhookKitRedis(
        this IServiceCollection services,
        IConnectionMultiplexer connectionMultiplexer,
        Action<RedisWebhookStoreOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);
        AddCommonRegistrations(services, configure);
        services.AddSingleton(connectionMultiplexer);
        services.AddSingleton<IWebhookStore, RedisWebhookStore>();
        return services;
    }

    public static IServiceCollection AddWebhookKitRedis(
        this IServiceCollection services,
        string connectionString)
    {
        return AddWebhookKitRedis(services, connectionString, null);
    }

    public static IServiceCollection AddWebhookKitRedis(
        this IServiceCollection services,
        string connectionString,
        Action<RedisWebhookStoreOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string must be non-empty.", nameof(connectionString));
        }

        AddCommonRegistrations(services, configure);
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connectionString));
        services.AddSingleton<IWebhookStore, RedisWebhookStore>();
        return services;
    }

    private static void AddCommonRegistrations(
        IServiceCollection services,
        Action<RedisWebhookStoreOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<RedisWebhookStoreOptions>()
            .Configure(options => configure?.Invoke(options))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RedisWebhookStoreOptions>, RedisWebhookStoreOptionsValidator>());
    }
}
