using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WebhookKit.Abstractions;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Stores;
using WebhookKit.Core.Workers;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.Core.Tests;

public sealed class ScopedStoreWorkerTests
{
    [Fact]
    public void ScopedStore_CanResolveTheDeduplicator()
    {
        var clock = new FakeWebhookClock(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<IWebhookClock>(clock);
        services.AddScoped<IWebhookStore>(_ => new InMemoryWebhookStore(clock));
        services.AddWebhookKit();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        Action resolve = () => scope.ServiceProvider.GetRequiredService<IWebhookDeduplicator>().Should().NotBeNull();

        resolve.Should().NotThrow();
    }

    [Fact]
    public void HostedWorker_CanBeResolvedWhenTheStoreIsScoped()
    {
        var clock = new FakeWebhookClock(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<IWebhookClock>(clock);
        services.AddScoped<IWebhookStore>(_ => new InMemoryWebhookStore(clock));
        services.AddWebhookKit(options => options.Background.Enabled = true);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Action resolve = () =>
        {
            var worker = provider.GetServices<IHostedService>().OfType<WebhookBackgroundWorker>().Single();
            worker.Should().NotBeNull();
        };

        resolve.Should().NotThrow();
    }
}
