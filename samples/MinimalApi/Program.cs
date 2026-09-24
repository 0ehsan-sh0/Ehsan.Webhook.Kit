using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MinimalApiSample;
using WebhookKit.AspNetCore.DependencyInjection;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Options;

var builder = WebApplication.CreateBuilder(args);
var secret = RequiredEnvironment(builder.Configuration, "WEBHOOKKIT_PROVIDER_SECRET");

builder.Services.AddWebhookKit(options => ConfigureProvider(options, secret));
builder.Services.AddWebhookKitAspNetCore();
builder.Services.AddWebhookHandler<OrderCreatedHandler>("order.created");

var app = builder.Build();
app.MapWebhook("/webhooks/sample", "sample-provider");
app.Run();
return;

static void ConfigureProvider(WebhookKitOptions options, string secret)
{
    options.AddProvider("sample-provider", provider =>
    {
        provider.Signature.HeaderName = "X-Webhook-Signature";
        provider.Signature.Algorithm = WebhookHashAlgorithm.HmacSha256;
        provider.Signature.Encoding = WebhookSignatureEncoding.Hex;
        provider.Signature.Input = WebhookSignatureInput.RawBody;
        provider.Signature.Secret = secret;
        provider.Timestamp.HeaderName = "X-Webhook-Timestamp";
        provider.Timestamp.Tolerance = TimeSpan.FromMinutes(5);
        provider.EventIdHeaderName = "X-Webhook-Event-Id";
        provider.EventTypeHeaderName = "X-Webhook-Event-Type";
    });
}

static string RequiredEnvironment(IConfiguration configuration, string name)
{
    var value = configuration[name];
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Environment variable '{name}' is required.");
    }

    return value;
}
