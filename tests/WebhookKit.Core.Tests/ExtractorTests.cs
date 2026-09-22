// Copyright (c) Ehsan. Licensed under the MIT License.
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Core.Extractors;
using WebhookKit.Core.Options;
using Xunit;

namespace WebhookKit.Core.Tests;

public sealed class ExtractorTests
{
    private const string ProviderName = "test-provider";

    private static IOptions<WebhookKitOptions> CreateOptions(Action<WebhookProviderOptions>? configure = null)
    {
        var options = new WebhookKitOptions();
        options.AddProvider(ProviderName, p => configure?.Invoke(p));
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    [Fact]
    public async Task HeaderEventIdExtractor_HeaderPresent_ExtractsValue()
    {
        var options = CreateOptions(p => p.EventIdHeaderName = "X-Event-ID");
        var extractor = new HeaderEventIdExtractor(options);

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>
            {
                ["X-Event-ID"] = ["evt_998877"]
            }
        };

        var result = await extractor.ExtractAsync(context);
        result.Should().Be("evt_998877");
    }

    [Fact]
    public async Task HeaderEventIdExtractor_HeaderMissing_ReturnsNull()
    {
        var options = CreateOptions(p => p.EventIdHeaderName = "X-Event-ID");
        var extractor = new HeaderEventIdExtractor(options);

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>()
        };

        var result = await extractor.ExtractAsync(context);
        result.Should().BeNull();
    }

    [Fact]
    public async Task JsonEventIdExtractor_RootProperty_ExtractsValue()
    {
        var extractor = new JsonEventIdExtractor("id");
        byte[] body = Encoding.UTF8.GetBytes("{\"id\":\"evt_12345\",\"status\":\"ok\"}");

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>()
        };

        var result = await extractor.ExtractAsync(context);
        result.Should().Be("evt_12345");
    }

    [Fact]
    public async Task JsonEventIdExtractor_DottedPath_ExtractsNestedValue()
    {
        var extractor = new JsonEventIdExtractor("data.object.id");
        byte[] body = Encoding.UTF8.GetBytes("{\"data\":{\"object\":{\"id\":\"ch_3N4xYz2eZvKYlo2C\"}}}");

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>()
        };

        var result = await extractor.ExtractAsync(context);
        result.Should().Be("ch_3N4xYz2eZvKYlo2C");
    }

    [Fact]
    public async Task JsonEventIdExtractor_MalformedJson_ReturnsNullWithoutThrowing()
    {
        var extractor = new JsonEventIdExtractor("id");
        byte[] body = Encoding.UTF8.GetBytes("{not-valid-json...");

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>()
        };

        var result = await extractor.ExtractAsync(context);
        result.Should().BeNull();
    }

    [Fact]
    public async Task CompositeWebhookEventIdExtractor_HeaderTakesPrecedenceOverJson()
    {
        var options = CreateOptions(p => p.EventIdHeaderName = "X-Event-ID");
        var headerExtractor = new HeaderEventIdExtractor(options);
        var jsonExtractor = new JsonEventIdExtractor("id");
        var composite = new CompositeWebhookEventIdExtractor([headerExtractor, jsonExtractor]);

        byte[] body = Encoding.UTF8.GetBytes("{\"id\":\"json_id_123\"}");

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>
            {
                ["X-Event-ID"] = ["header_id_456"]
            }
        };

        var result = await composite.ExtractAsync(context);
        result.Should().Be("header_id_456");
    }

    [Fact]
    public async Task CompositeWebhookEventIdExtractor_FallsBackToJsonWhenHeaderAbsent()
    {
        var options = CreateOptions(p => p.EventIdHeaderName = "X-Event-ID");
        var headerExtractor = new HeaderEventIdExtractor(options);
        var jsonExtractor = new JsonEventIdExtractor("id");
        var composite = new CompositeWebhookEventIdExtractor([headerExtractor, jsonExtractor]);

        byte[] body = Encoding.UTF8.GetBytes("{\"id\":\"json_id_123\"}");

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>()
        };

        var result = await composite.ExtractAsync(context);
        result.Should().Be("json_id_123");
    }

    [Fact]
    public async Task HeaderEventTypeExtractor_HeaderPresent_ExtractsValue()
    {
        var options = CreateOptions(p => p.EventTypeHeaderName = "X-GitHub-Event");
        var extractor = new HeaderEventTypeExtractor(options);

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>
            {
                ["X-GitHub-Event"] = ["pull_request"]
            }
        };

        var result = await extractor.ExtractAsync(context);
        result.Should().Be("pull_request");
    }

    [Fact]
    public async Task JsonEventTypeExtractor_DottedPath_ExtractsValue()
    {
        var extractor = new JsonEventTypeExtractor("event.type");
        byte[] body = Encoding.UTF8.GetBytes("{\"event\":{\"type\":\"invoice.payment_succeeded\"}}");

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>()
        };

        var result = await extractor.ExtractAsync(context);
        result.Should().Be("invoice.payment_succeeded");
    }

    [Fact]
    public async Task CompositeWebhookEventTypeExtractor_FallsBackToJsonWhenHeaderAbsent()
    {
        var options = CreateOptions(p => p.EventTypeHeaderName = "X-Event-Type");
        var headerExtractor = new HeaderEventTypeExtractor(options);
        var jsonExtractor = new JsonEventTypeExtractor("type");
        var composite = new CompositeWebhookEventTypeExtractor([headerExtractor, jsonExtractor]);

        byte[] body = Encoding.UTF8.GetBytes("{\"type\":\"order.fulfilled\"}");

        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>()
        };

        var result = await composite.ExtractAsync(context);
        result.Should().Be("order.fulfilled");
    }
}
