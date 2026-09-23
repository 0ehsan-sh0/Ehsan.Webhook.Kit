using System.Reflection;
using System.Text;
using FluentAssertions;
using WebhookKit.Abstractions;
using WebhookKit.Abstractions.Exceptions;
using Xunit;

namespace WebhookKit.Abstractions.Tests;

public sealed class WebhookContextTests
{
    [Fact]
    public void Constructor_CopiesRawBodyAndHeaders_AndPreservesMetadata()
    {
        var rawBody = new byte[] { 7 };
        var sourceHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Event-Id"] = ["evt_original"],
        };
        var deserializer = new FakeWebhookDeserializer((_, body) => new FirstPayload(body.Span[0]));
        var receivedAt = new DateTimeOffset(2026, 9, 24, 1, 30, 0, TimeSpan.Zero);
        var context = new WebhookContext(rawBody, deserializer)
        {
            WebhookId = "01K7ABC",
            Provider = "stripe",
            EventId = "evt_original",
            EventType = "payment.succeeded",
            ReceivedAt = receivedAt,
            ProviderTimestamp = receivedAt.AddSeconds(-5),
            Headers = sourceHeaders,
        };

        rawBody[0] = 9;
        sourceHeaders["X-Event-Id"][0] = "evt_mutated";
        sourceHeaders["X-Added"] = ["added"];

        context.WebhookId.Should().Be("01K7ABC");
        context.Provider.Should().Be("stripe");
        context.EventId.Should().Be("evt_original");
        context.EventType.Should().Be("payment.succeeded");
        context.ReceivedAt.Should().Be(receivedAt);
        context.ProviderTimestamp.Should().Be(receivedAt.AddSeconds(-5));
        context.Headers["x-event-id"].Should().Equal("evt_original");
        context.Headers.Should().NotContainKey("X-Added");
        context.GetPayload<FirstPayload>().Value.Should().Be(7);

        var mutableHeaders = context.Headers.Should().BeAssignableTo<IDictionary<string, string[]>>().Subject;
        mutableHeaders.IsReadOnly.Should().BeTrue();
        var addHeader = () => mutableHeaders.Add("X-Blocked", ["blocked"]);
        addHeader.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void PublicSurface_ExposesTypedPayloadAccess_WithoutRawBodyProperty()
    {
        var publicProperties = typeof(WebhookContext).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var publicFields = typeof(WebhookContext).GetFields(BindingFlags.Instance | BindingFlags.Public);
        var getPayload = typeof(WebhookContext).GetMethod(nameof(WebhookContext.GetPayload), BindingFlags.Instance | BindingFlags.Public);

        publicProperties.Should().NotContain(property => property.Name == "RawBody");
        publicFields.Should().NotContain(field => field.Name == "RawBody");
        getPayload.Should().NotBeNull();
        getPayload!.IsGenericMethodDefinition.Should().BeTrue();
        getPayload.GetParameters().Should().BeEmpty();
        getPayload.ReturnType.Should().Be(getPayload.GetGenericArguments().Single());
    }

    [Fact]
    public void GetPayload_WhenCalledRepeatedly_ReturnsCachedInstance()
    {
        var deserializer = new FakeWebhookDeserializer((_, _) => new FirstPayload(42));
        var context = CreateContext(deserializer);

        var first = context.GetPayload<FirstPayload>();
        var second = context.GetPayload<FirstPayload>();

        second.Should().BeSameAs(first);
        deserializer.CallCount.Should().Be(1);
    }

    [Fact]
    public void GetPayload_ForDifferentClosedTypes_DeserializesEachTypeSeparately()
    {
        var deserializer = new FakeWebhookDeserializer((type, _) => type == typeof(FirstPayload)
            ? new FirstPayload(42)
            : new SecondPayload("other"));
        var context = CreateContext(deserializer);

        var first = context.GetPayload<FirstPayload>();
        var second = context.GetPayload<SecondPayload>();

        first.Value.Should().Be(42);
        second.Value.Should().Be("other");
        deserializer.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetPayload_WhenCalledConcurrently_ReturnsOneDeserializedInstance()
    {
        var deserializer = new FakeWebhookDeserializer((_, _) =>
        {
            Thread.SpinWait(20_000);
            return new FirstPayload(42);
        });
        var context = CreateContext(deserializer);

        var payloads = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => context.GetPayload<FirstPayload>())));

        payloads.Should().OnlyContain(payload => ReferenceEquals(payload, payloads[0]));
        deserializer.CallCount.Should().Be(1);
    }

    [Fact]
    public void GetPayload_WhenDeserializationFails_ThrowsOnlyFixedSafeException_AndDoesNotCacheFailure()
    {
        var attempts = 0;
        var deserializer = new FakeWebhookDeserializer((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("parser detail must not escape");
            }

            return new FirstPayload(2);
        });
        var context = CreateContext(deserializer);

        var firstAttempt = () => context.GetPayload<FirstPayload>();
        var exception = firstAttempt.Should().ThrowExactly<WebhookPayloadException>().Which;
        var firstPayload = context.GetPayload<FirstPayload>();
        var secondPayload = context.GetPayload<FirstPayload>();

        exception.Message.Should().Be("The webhook payload could not be deserialized.");
        exception.InnerException.Should().BeNull();
        firstPayload.Value.Should().Be(2);
        secondPayload.Should().BeSameAs(firstPayload);
        deserializer.CallCount.Should().Be(2);
    }

    [Fact]
    public void GetPayload_WhenPayloadTypeIsUnsupported_ThrowsOnlyFixedSafeException()
    {
        var deserializer = new FakeWebhookDeserializer((type, _) => type == typeof(UnsupportedPayload)
            ? throw new NotSupportedException("type parser detail must not escape")
            : new FirstPayload(1));
        var context = CreateContext(deserializer);

        var act = () => context.GetPayload<UnsupportedPayload>();

        var exception = act.Should().ThrowExactly<WebhookPayloadException>().Which;
        exception.Message.Should().Be("The webhook payload could not be deserialized.");
        exception.InnerException.Should().BeNull();
    }

    [Fact]
    public void GetPayload_WhenDeserializerReturnsNull_ThrowsOnlyFixedSafeException()
    {
        var deserializer = new FakeWebhookDeserializer((_, _) => null);
        var context = CreateContext(deserializer);

        var act = () => context.GetPayload<FirstPayload>();

        var exception = act.Should().ThrowExactly<WebhookPayloadException>().Which;
        exception.Message.Should().Be("The webhook payload could not be deserialized.");
        exception.InnerException.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithNullDeserializer_ThrowsOnlyFixedSafeException()
    {
        var act = () => new WebhookContext(new byte[] { 1 }, null!)
        {
            WebhookId = "01K7ABC",
            Provider = "stripe",
            ReceivedAt = default,
            Headers = new Dictionary<string, string[]>(),
        };

        var exception = act.Should().ThrowExactly<WebhookPayloadException>().Which;
        exception.Message.Should().Be("The webhook payload could not be deserialized.");
        exception.InnerException.Should().BeNull();
    }

    [Fact]
    public void ToString_IncludesSafeIdentifiers_WithoutRawBodyOrHeaderValues()
    {
        var rawBody = Encoding.UTF8.GetBytes("raw-body-secret");
        var deserializer = new FakeWebhookDeserializer((_, _) => new FirstPayload(1));
        var context = new WebhookContext(rawBody, deserializer)
        {
            WebhookId = "01K7ABC",
            Provider = "stripe",
            EventId = "evt_safe",
            EventType = "payment.succeeded",
            ReceivedAt = new DateTimeOffset(2026, 9, 24, 1, 30, 0, TimeSpan.Zero),
            Headers = new Dictionary<string, string[]>
            {
                ["Authorization"] = ["header-secret"],
            },
        };

        var value = context.ToString();

        value.Should().Contain("01K7ABC");
        value.Should().Contain("stripe");
        value.Should().Contain("evt_safe");
        value.Should().Contain("payment.succeeded");
        value.Should().NotContain("raw-body-secret");
        value.Should().NotContain("header-secret");
    }

    private static WebhookContext CreateContext(IWebhookDeserializer deserializer)
    {
        return new WebhookContext(new byte[] { 1 }, deserializer)
        {
            WebhookId = "01K7ABC",
            Provider = "stripe",
            ReceivedAt = new DateTimeOffset(2026, 9, 24, 1, 30, 0, TimeSpan.Zero),
            Headers = new Dictionary<string, string[]>(),
        };
    }

    private sealed class FakeWebhookDeserializer(Func<Type, ReadOnlyMemory<byte>, object?> deserialize) : IWebhookDeserializer
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public T Deserialize<T>(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return (T)deserialize(typeof(T), rawBody)!;
        }
    }

    private sealed record FirstPayload(int Value);

    private sealed record SecondPayload(string Value);

    private sealed record UnsupportedPayload;
}
