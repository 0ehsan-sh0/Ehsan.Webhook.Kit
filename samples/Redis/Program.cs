using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RedisSample;
using WebhookKit.AspNetCore.DependencyInjection;
using WebhookKit.AspNetCore.Pipeline;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Options;
using WebhookKit.Redis;

var builder = WebApplication.CreateBuilder(args);
var secret = RequiredEnvironment(builder.Configuration, "WEBHOOKKIT_PROVIDER_SECRET");
var redisConnection = RequiredEnvironment(builder.Configuration, "WEBHOOKKIT_REDIS_CONNECTION");

builder.Services.AddWebhookKit(options =>
{
    ConfigureProvider(options, secret);
    options.Background.Enabled = true;
    options.Background.WorkerConcurrency = 1;
});
builder.Services.AddWebhookKitAspNetCore();
builder.Services.AddWebhookKitRedis(redisConnection, options => options.KeyPrefix = "webhookkit-sample");
builder.Services.AddWebhookHandler<OrderCreatedHandler>("order.created");

var app = builder.Build();
app.MapWebhook(
    "/webhooks/sample",
    new WebhookEndpointOptions("sample-provider")
    {
        Mode = WebhookProcessingMode.Asynchronous
    });
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
