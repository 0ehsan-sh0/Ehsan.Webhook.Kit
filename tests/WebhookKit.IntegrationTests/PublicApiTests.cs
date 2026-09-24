using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore;
using WebhookKit.AspNetCore.DependencyInjection;
using WebhookKit.AspNetCore.Mvc;
using WebhookKit.AspNetCore.Pipeline;
using WebhookKit.AspNetCore.Responses;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Options;
using WebhookKit.Core.Processing;
using WebhookKit.EntityFrameworkCore;
using WebhookKit.Redis;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.IntegrationTests;

public sealed class PublicApiTests
{
    [Fact]
    public async Task DocumentedConsumerSurface_CompilesAndResolvesWithExpectedLifetimes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        services.AddWebhookKit(options => options.AddProvider("payments", provider =>
        {
            provider.EventIdHeaderName = "X-Event-Id";
            provider.EventTypeHeaderName = "X-Event-Type";
            provider.Timestamp.AllowMissing = true;
        }));
        services.AddWebhookHandler<ApiHandler>("payment.completed");
        services.AddSingleton<IWebhookResponseFormatter, ApiResponseFormatter>();
        services.AddWebhookKitAspNetCore();
        services.AddWebhookKitRedis("localhost:6379", options => options.KeyPrefix = "public-api");
        services.AddDbContext<ApiDbContext>(options => options.UseInMemoryDatabase("public-api"));
        services.AddWebhookKitEntityFrameworkCore<ApiDbContext>();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IWebhookStore) && descriptor.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IWebhookStore) && descriptor.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IWebhookDispatchProcessor) && descriptor.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IWebhookEndpointService) && descriptor.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IWebhookBodyReader) && descriptor.Lifetime == ServiceLifetime.Singleton);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();
        var endpointService = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();
        var formatter = scope.ServiceProvider.GetRequiredService<IWebhookResponseFormatter>();
        var mvcFilter = scope.ServiceProvider.GetRequiredService<WebhookEndpointFilter>();
        var handler = scope.ServiceProvider.GetRequiredService<ApiHandler>();
        var store = scope.ServiceProvider.GetRequiredService<IWebhookStore>();

        processor.Should().NotBeNull();
        endpointService.Should().NotBeNull();
        formatter.Should().BeOfType<ApiResponseFormatter>();
        mvcFilter.Should().NotBeNull();
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(WebhookEndpointFilter) && descriptor.Lifetime == ServiceLifetime.Scoped);
        handler.Should().NotBeNull();
        store.Should().NotBeNull();
        store.GetType().Name.Should().Be("EfCoreWebhookStore`1");

        var context = new WebhookContext("{\"value\":42}"u8.ToArray(), new ApiDeserializer())
        {
            WebhookId = "01K7ABC",
            Provider = "payments",
            EventId = "evt_public_api",
            EventType = "payment.completed",
            ReceivedAt = DateTimeOffset.UtcNow,
            Headers = CreateHeaders()
        };
        var result = await processor.DispatchAsync(context);
        result.Status.Should().Be(WebhookDispatchStatus.Processed);

        await using (var harness = new WebhookTestHarness(new StaticHost()))
        {
            await harness.StartAsync();
            harness.CreateClient().Dispose();
        }

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IWebhookResponseFormatter, ApiResponseFormatter>();
        builder.Services.AddWebhookKit();
        builder.Services.AddWebhookKitAspNetCore();
        await using var app = builder.Build();
        MapDocumentedEndpoints(app);
    }

    [Fact]
    public void PublicApi_ExposesOnlyReplacementInterfacesForInfrastructureImplementations()
    {
        var exported = new[]
        {
            typeof(WebhookContext).Assembly,
            typeof(WebhookKitOptions).Assembly,
            typeof(IWebhookBodyReader).Assembly,
            typeof(RedisWebhookStoreOptions).Assembly,
            typeof(WebhookEntity).Assembly,
            typeof(WebhookTestRequestBuilder).Assembly
        }
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Select(type => type.FullName!)
            .ToArray();

        exported.Should().NotContain("WebhookKit.Abstractions.IWebhookProcessor");
        exported.Should().NotContain("WebhookKit.Core.Stores.InMemoryWebhookStore");
        exported.Should().NotContain("WebhookKit.Core.Queues.ChannelWebhookQueue");
        exported.Should().NotContain("WebhookKit.Core.Workers.WebhookBackgroundWorker");
        exported.Should().NotContain("WebhookKit.Core.Verifiers.HmacSignatureVerifier");
        exported.Should().NotContain("WebhookKit.Core.Verifiers.WebhookTimestampVerifier");
        exported.Should().NotContain("WebhookKit.Core.Processing.WebhookProcessor");
        exported.Should().NotContain("WebhookKit.Redis.RedisWebhookStore");
        exported.Should().NotContain("WebhookKit.EntityFrameworkCore.EfCoreWebhookStore`1");
        exported.Should().NotContain("WebhookKit.EntityFrameworkCore.ProviderNeutralWebhookUniqueConstraintDetector");

        var abstractionReferences = typeof(IWebhookStore).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        abstractionReferences.Any(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)).Should().BeFalse();
        var coreReferences = typeof(WebhookKitOptions).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        coreReferences.Any(name =>
            name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
            name.StartsWith("StackExchange.Redis", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)).Should().BeFalse();
    }

    [Fact]
    public void PublicApi_HeaderAndCorrelationContractsAreRuntimeVerifiable()
    {
        var headerProperties = new[]
        {
            typeof(WebhookContext).GetProperty(nameof(WebhookContext.Headers)),
            typeof(WebhookRecord).GetProperty(nameof(WebhookRecord.Headers)),
            typeof(WebhookVerificationContext).GetProperty(nameof(WebhookVerificationContext.Headers)),
            typeof(WebhookIngestionRequest).GetProperty(nameof(WebhookIngestionRequest.Headers)),
            typeof(WebhookTestRequestBuilder).GetProperty(nameof(WebhookTestRequestBuilder.Headers))
        };

        foreach (var property in headerProperties)
        {
            property.Should().NotBeNull();
            (property!.PropertyType == typeof(IReadOnlyDictionary<string, IReadOnlyList<string>>)).Should().BeTrue();
        }

        var correlationProperties = new[]
        {
            typeof(WebhookContext).GetProperty(nameof(WebhookContext.CorrelationId)),
            typeof(WebhookRecord).GetProperty(nameof(WebhookRecord.CorrelationId))
        };
        var nullability = new NullabilityInfoContext();
        foreach (var property in correlationProperties)
        {
            property.Should().NotBeNull();
            (property!.PropertyType == typeof(string)).Should().BeTrue();
            nullability.Create(property).WriteState.Should().Be(NullabilityState.NotNull);
        }

        var requestCorrelation = typeof(WebhookIngestionRequest).GetProperty(nameof(WebhookIngestionRequest.CorrelationId));
        requestCorrelation.Should().NotBeNull();
        nullability.Create(requestCorrelation!).WriteState.Should().Be(NullabilityState.Nullable);

        var sourceHeaders = new Dictionary<string, IReadOnlyList<string>>
        {
            ["X-Event-Id"] = new List<string> { "evt_source" }
        };
        var context = new WebhookContext
        {
            WebhookId = "01K7ABC",
            Provider = "payments",
            ReceivedAt = DateTimeOffset.UtcNow,
            Headers = sourceHeaders
        };
        var values = context.Headers["X-Event-Id"];
        var mutate = () => ((IList<string>)values).Add("blocked");
        mutate.Should().Throw<NotSupportedException>();
        ((List<string>)sourceHeaders["X-Event-Id"]).Add("changed");
        context.Headers["X-Event-Id"].Should().Equal("evt_source");
        context.CorrelationId.Should().NotBeNullOrWhiteSpace();
        context.CorrelationId.Should().NotBe(context.WebhookId);

        var defaultOptions = new WebhookEndpointOptions("payments");
        var defaultTags = (IList<string>)defaultOptions.Tags;
        var mutateDefaultTag = () => defaultTags.Add("blocked");
        mutateDefaultTag.Should().Throw<NotSupportedException>();
        (defaultOptions.Tags is string[]).Should().BeFalse();

        var sourceTags = new[] { "webhooks" };
        var options = new WebhookEndpointOptions("payments") { Tags = sourceTags };
        (options.Tags is string[]).Should().BeFalse();
        var tags = (IList<string>)options.Tags;
        var mutateTag = () => tags.Add("blocked");
        mutateTag.Should().Throw<NotSupportedException>();
        sourceTags[0] = "changed";
        options.Tags.Should().Equal("webhooks");
    }

    [Fact]
    public void PublicApi_DispatchAndStoreContractsUseCancellationAndResultShapes()
    {
        var dispatch = typeof(IWebhookDispatchProcessor).GetMethod(nameof(IWebhookDispatchProcessor.DispatchAsync));
        dispatch.Should().NotBeNull();
        (dispatch!.ReturnType == typeof(Task<WebhookDispatchResult>)).Should().BeTrue();
        dispatch.GetParameters().Any(parameter => parameter.ParameterType == typeof(CancellationToken)).Should().BeTrue();

        var store = typeof(IWebhookStore).GetMethod(nameof(IWebhookStore.GetByWebhookIdAsync));
        store.Should().NotBeNull();
        (store!.ReturnType == typeof(ValueTask<WebhookRecord?>)).Should().BeTrue();
        store.GetParameters().Should().Contain(parameter => parameter.ParameterType == typeof(CancellationToken));
    }

    private static void MapDocumentedEndpoints(IEndpointRouteBuilder routes)
    {
        routes.MapWebhook("/webhooks/payments", "payments");
        routes.MapWebhook("/webhooks/payments-advanced", new WebhookEndpointOptions("payments")
        {
            Mode = WebhookProcessingMode.Asynchronous,
            IncludeInSchema = false,
            Response = new WebhookEndpointResponseOptions
            {
                SuccessStatusCode = StatusCodes.Status200OK,
                AcceptedStatusCode = StatusCodes.Status202Accepted
            }
        });
    }

    private static Dictionary<string, IReadOnlyList<string>> CreateHeaders()
    {
        return new Dictionary<string, IReadOnlyList<string>>
        {
            ["X-Event-Id"] = new[] { "evt_public_api" },
            ["X-Event-Type"] = new[] { "payment.completed" }
        };
    }

    private sealed record ApiPayload(int Value);

    private sealed class ApiHandler : IWebhookHandler<ApiPayload>
    {
        public Task HandleAsync(ApiPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ApiDeserializer : IWebhookDeserializer
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public T Deserialize<T>(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (T)(object)JsonSerializer.Deserialize<ApiPayload>(rawBody.Span, SerializerOptions)!;
        }
    }

    private sealed class ApiResponseFormatter : IWebhookResponseFormatter
    {
        public WebhookResponseProblem Format(WebhookEndpointOutcome outcome, string? traceId = null)
        {
            return new WebhookResponseProblem("api-test", "API test response.", traceId);
        }
    }

    private sealed class ApiDbContext(DbContextOptions<ApiDbContext> options) : DbContext(options)
    {
        public DbSet<WebhookEntity> WebhookEntities => Set<WebhookEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyWebhookConfiguration();
        }
    }

    private sealed class StaticHost : IWebhookTestHost
    {
        public HttpClient CreateClient() => new();

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
