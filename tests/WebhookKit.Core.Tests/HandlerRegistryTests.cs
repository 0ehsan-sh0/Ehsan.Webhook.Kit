using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using WebhookKit.Abstractions;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Handlers;
using WebhookKit.Core.Options;
using Xunit;

namespace WebhookKit.Core.Tests;

public sealed class HandlerRegistryTests
{
    [Fact]
    public void AddWebhookHandler_RegistersScopedImplementationAndPreservesRegistrationOrder()
    {
        var services = new ServiceCollection();
        services.AddWebhookKit();
        services.AddWebhookHandler<FirstHandler>("event.type");
        services.AddWebhookHandler<SecondHandler>("event.type");
        services.AddWebhookHandler<FirstHandler>("event.type");

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<WebhookHandlerRegistry>();

        registry.GetHandlers("event.type")
            .Select(descriptor => descriptor.ImplementationType)
            .Should()
            .Equal(typeof(FirstHandler), typeof(SecondHandler), typeof(FirstHandler));
        registry.GetHandlers("event.type")
            .Select(descriptor => descriptor.PayloadType)
            .Should()
            .Equal(typeof(TestPayload), typeof(TestPayload), typeof(TestPayload));
        services.Count(service =>
            service.ServiceType == typeof(FirstHandler) && service.Lifetime == ServiceLifetime.Scoped)
            .Should()
            .Be(2);
        services.Should().ContainSingle(service =>
            service.ServiceType == typeof(SecondHandler) && service.Lifetime == ServiceLifetime.Scoped);

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<FirstHandler>().Should().NotBeNull();
    }

    [Fact]
    public void HandlerRegistry_UsesOrdinalMatchingWithoutTrimmingOrCaseFolding()
    {
        var services = new ServiceCollection();
        services.AddWebhookHandler<FirstHandler>(" Event.Type ");
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<WebhookHandlerRegistry>();

        registry.GetHandlers(" Event.Type ").Should().ContainSingle();
        registry.GetHandlers("Event.Type").Should().BeEmpty();
        registry.GetHandlers(" event.type ").Should().BeEmpty();
        registry.GetHandlers("event.type").Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddWebhookHandler_WithInvalidEventType_RejectsRegistration(string? eventType)
    {
        var services = new ServiceCollection();

        var act = () => services.AddWebhookHandler<FirstHandler>(eventType!);

        act.Should().Throw<WebhookConfigurationException>();
        services.Should().NotContain(service => service.ServiceType == typeof(WebhookHandlerDescriptor));
    }

    [Fact]
    public void AddWebhookHandler_WithInvalidImplementationType_RejectsEveryUnsupportedShape()
    {
        var services = new ServiceCollection();
        var invalidTypes = new[]
        {
            typeof(AbstractHandler),
            typeof(NoHandler),
            typeof(MultipleHandler),
            typeof(OpenHandler<>),
            typeof(PrivateConstructorHandler),
            typeof(IWebhookHandler<TestPayload>)
        };

        foreach (var implementationType in invalidTypes)
        {
            var act = () => services.AddWebhookHandler(implementationType, "event.type");

            act.Should().Throw<WebhookConfigurationException>();
            services.Should().NotContain(service => service.ServiceType == implementationType);
        }
    }

    [Fact]
    public void AddWebhookHandler_AcceptsOpaqueEventTypesContainingNonWhitespaceEdges()
    {
        var services = new ServiceCollection();
        services.AddWebhookHandler<FirstHandler>(" event.type ");

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<WebhookHandlerRegistry>();

        registry.GetHandlers(" event.type ").Should().ContainSingle();
    }

    public sealed record TestPayload(string Value);

    public sealed class FirstHandler : IWebhookHandler<TestPayload>
    {
        public Task HandleAsync(TestPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class SecondHandler : IWebhookHandler<TestPayload>
    {
        public Task HandleAsync(TestPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    public abstract class AbstractHandler : IWebhookHandler<TestPayload>
    {
        public abstract Task HandleAsync(TestPayload eventData, WebhookContext context, CancellationToken cancellationToken = default);
    }

    public sealed class NoHandler
    {
    }

    public sealed class MultipleHandler : IWebhookHandler<TestPayload>, IWebhookHandler<object>
    {
        public Task HandleAsync(TestPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task HandleAsync(object eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class OpenHandler<TPayload> : IWebhookHandler<TPayload>
    {
        public Task HandleAsync(TPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class PrivateConstructorHandler : IWebhookHandler<TestPayload>
    {
        private PrivateConstructorHandler()
        {
        }

        public Task HandleAsync(TestPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
