using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Abstractions.Exceptions;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Deserialization;
using WebhookKit.Core.Options;
using Xunit;

namespace WebhookKit.Core.Tests;

public sealed class WebhookDeserializerTests
{
    private const string SafePayloadExceptionMessage = "The webhook payload could not be deserialized.";

    [Fact]
    public void Deserialize_ValidJson_ReturnsTypedPayload()
    {
        var deserializer = CreateDeserializer();

        var result = deserializer.Deserialize<TestPayload>("{\"eventId\":\"evt_valid\",\"attempt\":2}"u8.ToArray());

        result.Should().Be(new TestPayload("evt_valid", 2));
    }

    [Fact]
    public void Deserialize_DefaultOptions_MatchesPropertyNamesCaseInsensitively()
    {
        var deserializer = CreateDeserializer();

        var result = deserializer.Deserialize<TestPayload>("{\"EVENTID\":\"evt_case\",\"ATTEMPT\":3}"u8.ToArray());

        result.Should().Be(new TestPayload("evt_case", 3));
    }

    [Fact]
    public void AddWebhookKit_AppliesCustomJsonOptionsToDeserializer()
    {
        var services = new ServiceCollection();
        services.AddWebhookKit(options =>
        {
            options.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
        });

        var configuredOptions = new WebhookKitOptions();
        foreach (var configure in services
                     .Select(descriptor => descriptor.ImplementationInstance)
                     .OfType<IConfigureOptions<WebhookKitOptions>>())
        {
            configure.Configure(configuredOptions);
        }

        var deserializer = new SystemTextJsonWebhookDeserializer(Microsoft.Extensions.Options.Options.Create(configuredOptions));
        var result = deserializer.Deserialize<TestPayload>("{\"eventId\":\"evt_custom\",\"attempt\":\"4\"}"u8.ToArray());

        result.Should().Be(new TestPayload("evt_custom", 4));

        var wrongCaseResult = deserializer.Deserialize<TestPayload>("{\"EVENTID\":\"evt_wrong_case\",\"attempt\":5}"u8.ToArray());
        wrongCaseResult.EventId.Should().BeNull();
        wrongCaseResult.Attempt.Should().Be(5);
    }

    [Fact]
    public void Deserialize_MalformedJson_ThrowsOnlyFixedSafeException()
    {
        var deserializer = CreateDeserializer();
        var rawBody = "{\"eventId\":\"sensitive-event\",\"attempt\":"u8.ToArray();

        var act = () => deserializer.Deserialize<TestPayload>(rawBody);

        AssertSafePayloadException(act, "sensitive-event", "JsonReaderException");
    }

    [Fact]
    public void Deserialize_InvalidUtf8_ThrowsOnlyFixedSafeException()
    {
        var deserializer = CreateDeserializer();
        byte[] rawBody = [0x7b, 0x22, 0x65, 0x76, 0x65, 0x6e, 0x74, 0x49, 0x64, 0x22, 0x3a, 0x22, 0xff, 0x22, 0x7d];

        var act = () => deserializer.Deserialize<TestPayload>(rawBody);

        AssertSafePayloadException(act, "0xFF", "Cannot transcode");
    }

    [Fact]
    public void Deserialize_EmptyJson_ThrowsOnlyFixedSafeException()
    {
        var deserializer = CreateDeserializer();

        var act = () => deserializer.Deserialize<TestPayload>(ReadOnlyMemory<byte>.Empty);

        AssertSafePayloadException(act);
    }

    [Fact]
    public void Deserialize_NullJsonResult_ThrowsOnlyFixedSafeException()
    {
        var deserializer = CreateDeserializer();

        var act = () => deserializer.Deserialize<TestPayload>("null"u8.ToArray());

        AssertSafePayloadException(act, "null");
    }

    [Fact]
    public void Deserialize_PreCancelledToken_ThrowsAssociatedCancellationBeforeParsing()
    {
        var deserializer = CreateDeserializer();
        using var source = new CancellationTokenSource();
        source.Cancel();

        var act = () => deserializer.Deserialize<TestPayload>("{"u8.ToArray(), source.Token);

        var exception = act.Should().ThrowExactly<OperationCanceledException>().Which;
        exception.CancellationToken.Should().Be(source.Token);
    }

    [Fact]
    public void AddWebhookKit_RegistersDeserializerAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddWebhookKit();

        var descriptor = services
            .Should()
            .ContainSingle(service => service.ServiceType == typeof(IWebhookDeserializer))
            .Which;
        descriptor.ImplementationType.Should().Be<SystemTextJsonWebhookDeserializer>();
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddWebhookKit_PreservesCustomDeserializerRegistration()
    {
        var services = new ServiceCollection();
        var customDeserializer = new CustomWebhookDeserializer();
        services.AddSingleton<IWebhookDeserializer>(customDeserializer);

        services.AddWebhookKit();

        var descriptor = services
            .Should()
            .ContainSingle(service => service.ServiceType == typeof(IWebhookDeserializer))
            .Which;
        descriptor.ImplementationInstance.Should().BeSameAs(customDeserializer);
    }

    private static SystemTextJsonWebhookDeserializer CreateDeserializer()
    {
        return new SystemTextJsonWebhookDeserializer(Microsoft.Extensions.Options.Options.Create(new WebhookKitOptions()));
    }

    private static WebhookPayloadException AssertSafePayloadException<T>(Func<T> act, params string[] sensitiveDetails)
    {
        var exception = act.Should().ThrowExactly<WebhookPayloadException>().Which;
        exception.Message.Should().Be(SafePayloadExceptionMessage);
        exception.InnerException.Should().BeNull();

        foreach (var detail in sensitiveDetails)
        {
            exception.Message.Should().NotContain(detail);
        }

        return exception;
    }

    private sealed record TestPayload(string EventId, int Attempt);

    private sealed class CustomWebhookDeserializer : IWebhookDeserializer
    {
        public T Deserialize<T>(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
