using System.Text.Json;
using FluentAssertions;
using WebhookKit.Abstractions;
using WebhookKit.Abstractions.Exceptions;
using Xunit;

namespace WebhookKit.Core.Tests;

public sealed class ContextPayloadTests
{
    [Fact]
    public void GetPayload_WithCoreDeserializer_CachesEachClosedType()
    {
        var deserializer = new FakeJsonWebhookDeserializer();
        var context = CreateContext("{\"value\":42}"u8.ToArray(), deserializer);

        var first = context.GetPayload<FirstCorePayload>();
        var repeated = context.GetPayload<FirstCorePayload>();
        var second = context.GetPayload<SecondCorePayload>();

        repeated.Should().BeSameAs(first);
        first.Value.Should().Be(42);
        second.Value.Should().Be("42");
        deserializer.CallCount.Should().Be(2);
    }

    [Fact]
    public void GetPayload_WithInvalidJson_ThrowsOnlyFixedSafeException()
    {
        var deserializer = new FakeJsonWebhookDeserializer();
        var context = CreateContext("not-json"u8.ToArray(), deserializer);

        var act = () => context.GetPayload<FirstCorePayload>();

        var exception = act.Should().ThrowExactly<WebhookPayloadException>().Which;
        exception.Message.Should().Be("The webhook payload could not be deserialized.");
        exception.InnerException.Should().BeNull();
    }

    [Fact]
    public void GetPayload_WithoutCancellation_PassesNonCancelableTokenToCoreDeserializer()
    {
        var deserializer = new FakeJsonWebhookDeserializer();
        var context = CreateContext("{\"value\":42}"u8.ToArray(), deserializer);

        context.GetPayload<FirstCorePayload>();

        deserializer.LastCancellationToken.CanBeCanceled.Should().BeFalse();
    }

    private static WebhookContext CreateContext(byte[] rawBody, IWebhookDeserializer deserializer)
    {
        return new WebhookContext(rawBody, deserializer)
        {
            WebhookId = "01K7ABC",
            Provider = "stripe",
            ReceivedAt = new DateTimeOffset(2026, 9, 24, 1, 30, 0, TimeSpan.Zero),
            Headers = new Dictionary<string, string[]>(),
        };
    }

    private sealed class FakeJsonWebhookDeserializer : IWebhookDeserializer
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public CancellationToken LastCancellationToken { get; private set; }

        public T Deserialize<T>(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            LastCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();

            using var document = JsonDocument.Parse(rawBody);
            var value = document.RootElement.GetProperty("value").ToString();

            if (typeof(T) == typeof(FirstCorePayload))
            {
                return (T)(object)new FirstCorePayload(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
            }

            if (typeof(T) == typeof(SecondCorePayload))
            {
                return (T)(object)new SecondCorePayload(value);
            }

            throw new JsonException("unsupported payload type parser detail");
        }
    }

    private sealed record FirstCorePayload(int Value);

    private sealed record SecondCorePayload(string Value);
}
