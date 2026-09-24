using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WebhookKit.Abstractions;

namespace WebhookKit.Redis;

/// <summary>Registers the Redis-backed webhook store and its options.</summary>
public static class RedisServiceCollectionExtensions
{
    /// <summary>Registers Redis storage using an existing connection multiplexer.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="connectionMultiplexer">The application's Redis connection multiplexer.</param>
    /// <returns>The same service collection for fluent registration.</returns>
    public static IServiceCollection AddWebhookKitRedis(
        this IServiceCollection services,
        IConnectionMultiplexer connectionMultiplexer)
    {
        return AddWebhookKitRedis(services, connectionMultiplexer, null);
    }

    /// <summary>Registers Redis storage using an existing connection multiplexer and options callback.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="connectionMultiplexer">The application's Redis connection multiplexer.</param>
    /// <param name="configure">Optional callback for key prefix and retention settings.</param>
    /// <returns>The same service collection for fluent registration.</returns>
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

    /// <summary>Registers Redis storage using a connection string.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="connectionString">Redis connection string; connection creation is deferred until resolution.</param>
    /// <returns>The same service collection for fluent registration.</returns>
    public static IServiceCollection AddWebhookKitRedis(
        this IServiceCollection services,
        string connectionString)
    {
        return AddWebhookKitRedis(services, connectionString, null);
    }

    /// <summary>Registers Redis storage using a connection string and options callback.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="connectionString">Redis connection string; connection creation is deferred until resolution.</param>
    /// <param name="configure">Optional callback for key prefix and retention settings.</param>
    /// <returns>The same service collection for fluent registration.</returns>
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
