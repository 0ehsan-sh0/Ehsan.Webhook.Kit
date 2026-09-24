using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using WebhookKit.Abstractions;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Options;

namespace WebhookKit.Benchmarks;

[ShortRunJob]
public class WebhookKitBenchmarks
{
    private const string SignatureHeaderName = "X-Webhook-Signature";
    private const int AcceptedOperationCount = 64;
    private const int AcceptedRecordCount = AcceptedOperationCount * 32;
    private readonly string _secret = "benchmark-only-secret-for-fixed-input";
    private readonly string _providerName = "benchmark";
    private readonly string _webhookId = "01JWEBHOOKKITBENCHMARK";
    private readonly string _eventId = "evt_benchmark";
    private readonly string _eventType = "benchmark.created";
    private ServiceProvider? _primaryProvider;
    private ServiceProvider? _acceptedProvider;
    private IWebhookSignatureVerifier _signatureVerifier = null!;
    private IWebhookDeserializer _deserializer = null!;
    private IWebhookStore _duplicateStore = null!;
    private IWebhookStore _acceptedStore = null!;
    private JsonSerializerOptions _jsonOptions = null!;
    private byte[] _rawBody = null!;
    private Dictionary<string, IReadOnlyList<string>> _headers = null!;
    private WebhookVerificationContext _sha256Context = null!;
    private WebhookVerificationContext _sha512Context = null!;
    private WebhookRecord _duplicateRecord = null!;
    private WebhookRecord[] _acceptedRecords = null!;
    private DateTimeOffset _receivedAt;
    private int _acceptedRecordIndex;

    [GlobalSetup]
    public async Task Setup()
    {
        _receivedAt = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        _rawBody = Encoding.UTF8.GetBytes("{\"eventId\":\"evt_benchmark\",\"attempt\":\"2\",\"status\":\"processed\"}");
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };
        _headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = new[] { "application/json" },
            ["X-Webhook-Event-Id"] = new[] { _eventId },
            ["X-Webhook-Event-Type"] = new[] { _eventType }
        };

        _primaryProvider = BuildServiceProvider();
        _signatureVerifier = _primaryProvider.GetRequiredService<IWebhookSignatureVerifier>();
        _deserializer = _primaryProvider.GetRequiredService<IWebhookDeserializer>();
        _duplicateStore = _primaryProvider.GetRequiredService<IWebhookStore>();

        var sha256Signature = CreateSignature(WebhookHashAlgorithm.HmacSha256);
        var sha512Signature = CreateSignature(WebhookHashAlgorithm.HmacSha512);
        _sha256Context = CreateVerificationContext("benchmark-sha256", sha256Signature);
        _sha512Context = CreateVerificationContext("benchmark-sha512", sha512Signature);

        if (!(await _signatureVerifier.VerifyAsync(_sha256Context)).IsValid ||
            !(await _signatureVerifier.VerifyAsync(_sha512Context)).IsValid)
        {
            throw new InvalidOperationException("Benchmark signature setup failed.");
        }

        var payload = _deserializer.Deserialize<BenchmarkPayload>(_rawBody);
        if (payload.EventId != _eventId || payload.Attempt != 2 || payload.Status != "processed")
        {
            throw new InvalidOperationException("Benchmark payload setup failed.");
        }

        _acceptedRecords = Enumerable.Range(0, AcceptedRecordCount)
            .Select(index => CreateRecord($"webhook-accepted-{index}", $"evt_accepted_{index}"))
            .ToArray();
        _duplicateRecord = CreateRecord("webhook-duplicate", "evt_duplicate");
        if (!await _duplicateStore.TryCreateAsync(_duplicateRecord))
        {
            throw new InvalidOperationException("Benchmark duplicate setup failed.");
        }

        _acceptedProvider = BuildServiceProvider();
        _acceptedStore = _acceptedProvider.GetRequiredService<IWebhookStore>();
        _acceptedRecordIndex = 0;
    }

    [IterationSetup(Target = nameof(AtomicCreateAccepted))]
    public void ResetAcceptedStore()
    {
        _acceptedProvider?.Dispose();
        _acceptedProvider = BuildServiceProvider();
        _acceptedStore = _acceptedProvider.GetRequiredService<IWebhookStore>();
        _acceptedRecordIndex = 0;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _primaryProvider?.Dispose();
        _acceptedProvider?.Dispose();
    }

    [Benchmark]
    public ValueTask<WebhookVerificationResult> VerifyHmacSha256()
    {
        return _signatureVerifier.VerifyAsync(_sha256Context);
    }

    [Benchmark]
    public ValueTask<WebhookVerificationResult> VerifyHmacSha512()
    {
        return _signatureVerifier.VerifyAsync(_sha512Context);
    }

    [Benchmark]
    public BenchmarkPayload DeserializeJson()
    {
        return _deserializer.Deserialize<BenchmarkPayload>(_rawBody);
    }

    [Benchmark]
    [InvocationCount(AcceptedOperationCount)]
    public ValueTask<bool> AtomicCreateAccepted()
    {
        return _acceptedStore.TryCreateAsync(_acceptedRecords[_acceptedRecordIndex++]);
    }

    [Benchmark]
    public ValueTask<bool> AtomicCreateDuplicate()
    {
        return _duplicateStore.TryCreateAsync(_duplicateRecord);
    }

    [Benchmark]
    public WebhookVerificationContext ConstructVerificationContext()
    {
        return new WebhookVerificationContext
        {
            Provider = _providerName,
            RawBody = _rawBody,
            Headers = _headers
        };
    }

    [Benchmark]
    public WebhookContext ConstructWebhookContext()
    {
        return new WebhookContext(_rawBody, _deserializer)
        {
            WebhookId = _webhookId,
            Provider = _providerName,
            EventId = _eventId,
            EventType = _eventType,
            ReceivedAt = _receivedAt,
            Headers = _headers
        };
    }

    private ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddWebhookKit(options =>
        {
            options.JsonSerializerOptions = _jsonOptions;
            options.AddProvider("benchmark-sha256", provider => ConfigureSignature(provider, WebhookHashAlgorithm.HmacSha256));
            options.AddProvider("benchmark-sha512", provider => ConfigureSignature(provider, WebhookHashAlgorithm.HmacSha512));
        });

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private void ConfigureSignature(WebhookProviderOptions provider, WebhookHashAlgorithm algorithm)
    {
        provider.Signature.HeaderName = SignatureHeaderName;
        provider.Signature.Algorithm = algorithm;
        provider.Signature.Encoding = WebhookSignatureEncoding.Hex;
        provider.Signature.Input = WebhookSignatureInput.RawBody;
        provider.Signature.Secret = _secret;
        provider.Timestamp.AllowMissing = true;
    }

    private WebhookVerificationContext CreateVerificationContext(string provider, string signature)
    {
        return new WebhookVerificationContext
        {
            Provider = provider,
            RawBody = _rawBody,
            Headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = new[] { "application/json" },
                [SignatureHeaderName] = new[] { signature }
            }
        };
    }

    private string CreateSignature(WebhookHashAlgorithm algorithm)
    {
        var secretBytes = Encoding.UTF8.GetBytes(_secret);
        var hash = algorithm == WebhookHashAlgorithm.HmacSha256
            ? HMACSHA256.HashData(secretBytes, _rawBody)
            : HMACSHA512.HashData(secretBytes, _rawBody);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private WebhookRecord CreateRecord(string id, string eventId)
    {
        return new WebhookRecord
        {
            Id = id,
            CorrelationId = $"correlation-{id}",
            Provider = _providerName,
            EventId = eventId,
            DeduplicationKey = $"{_providerName}:{eventId}",
            HttpMethod = "POST",
            RequestPath = "/webhooks/benchmark",
            Headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = new[] { "application/json" },
                ["X-Webhook-Event-Id"] = new[] { eventId }
            },
            RawBody = _rawBody,
            ReceivedAt = _receivedAt,
            Status = WebhookProcessingStatus.Received
        };
    }
}

public sealed record BenchmarkPayload(string EventId, int Attempt, string Status);
