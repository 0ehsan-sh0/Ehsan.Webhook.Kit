using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebhookKit.Abstractions;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Options;
using WebhookKit.Core.Processing;
using WebhookKit.Core.Verifiers;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.Core.Tests;

[CollectionDefinition("WebhookSecurity", DisableParallelization = true)]
public sealed class WebhookSecurityTestGroup
{
}

[Collection("WebhookSecurity")]
public sealed class WebhookSecurityTests
{
    private const string ProviderName = "security-core-provider";
    private const string SignatureHeader = "X-Webhook-Signature";
    private const string TimestampHeader = "X-Webhook-Timestamp";
    private const string EventIdHeader = "X-Webhook-Event-Id";
    private const string EventTypeHeader = "X-Webhook-Event-Type";
    private const string GenericSignatureFailure = "Signature verification failed.";
    private const string BodyMarker = "sec-audit-raw-body-marker-8d71";
    private const string HeaderMarker = "sec-audit-authorization-marker-43c2";
    private const string SignatureMarker = "sec-audit-signature-marker-65af";
    private const string ConfiguredSecretMarker = "sec-audit-configured-secret-marker-29e4";
    private const string RotationSecretMarker = "sec-audit-rotation-secret-marker-71b6";
    private const string VerifierReasonMarker = "sec-audit-verifier-reason-marker-90af";
    private const string ParserDetailMarker = "sec-audit-parser-detail-marker-17cd";
    private const string HandlerMessageMarker = "sec-audit-handler-message-marker-a4e8";
    private const string StackTextMarker = "sec-audit-stack-text-marker-f3b5";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<WebhookHashAlgorithm, string> SignatureFailureCases => new()
    {
        { WebhookHashAlgorithm.HmacSha256, "tampered" },
        { WebhookHashAlgorithm.HmacSha256, "malformed" },
        { WebhookHashAlgorithm.HmacSha256, "wrong-secret" },
        { WebhookHashAlgorithm.HmacSha256, "encoding-mismatch" },
        { WebhookHashAlgorithm.HmacSha512, "tampered" },
        { WebhookHashAlgorithm.HmacSha512, "malformed" },
        { WebhookHashAlgorithm.HmacSha512, "wrong-secret" },
        { WebhookHashAlgorithm.HmacSha512, "encoding-mismatch" }
    };

    [Theory]
    [MemberData(nameof(SignatureFailureCases))]
    public async Task HmacVerifier_AllAlgorithmsAndFailureModes_ReturnOneIndistinguishableSafeResult(
        WebhookHashAlgorithm algorithm,
        string failureMode)
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"event\":\"security\"}");
        var options = new WebhookKitOptions();
        options.AddProvider(ProviderName, provider =>
        {
            provider.Signature.HeaderName = SignatureHeader;
            provider.Signature.Secret = ConfiguredSecretMarker;
            provider.Signature.AdditionalSecrets.Add(RotationSecretMarker);
            provider.Signature.Algorithm = algorithm;
            provider.Signature.Encoding = WebhookSignatureEncoding.Hex;
        });
        var verifier = new HmacSignatureVerifier(Microsoft.Extensions.Options.Options.Create(options));
        string signature = CreateFailureSignature(body, algorithm, failureMode);

        var result = await verifier.VerifyAsync(new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = body,
            Headers = new Dictionary<string, string[]>
            {
                [SignatureHeader] = [signature]
            }
        });

        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be(GenericSignatureFailure);
        result.ToString().Should().Contain(GenericSignatureFailure);
        AssertNoMarkers(result.ToString(), BodyMarker, HeaderMarker, SignatureMarker, ConfiguredSecretMarker, RotationSecretMarker, VerifierReasonMarker, ParserDetailMarker, HandlerMessageMarker, StackTextMarker);
    }

    [Theory]
    [MemberData(nameof(SignatureFailureCases))]
    public void SignatureGenerator_AllAlgorithmsAndFailureModes_ReturnOneIndistinguishableSafeResult(
        WebhookHashAlgorithm algorithm,
        string failureMode)
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"event\":\"security\"}");
        var generator = new WebhookSignatureGenerator(
            ConfiguredSecretMarker,
            algorithm,
            WebhookSignatureEncoding.Hex,
            rotationSecrets: [RotationSecretMarker]);
        string signature = CreateFailureSignature(body, algorithm, failureMode);

        var result = generator.VerifyResult(signature, body);

        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be(GenericSignatureFailure);
        result.ToString().Should().Contain(GenericSignatureFailure);
        AssertNoMarkers(result.ToString(), BodyMarker, HeaderMarker, SignatureMarker, ConfiguredSecretMarker, RotationSecretMarker, VerifierReasonMarker, ParserDetailMarker, HandlerMessageMarker, StackTextMarker);
    }

    [Fact]
    public async Task TimestampVerifier_ExactToleranceBoundaries_AcceptsBoundaryAndRejectsOneTickOutside()
    {
        var (verifier, _) = CreateTimestampVerifier();
        var tolerance = TimeSpan.FromMinutes(5);

        foreach (var boundary in new[] { FixedNow - tolerance, FixedNow + tolerance })
        {
            var result = await VerifyTimestampAsync(verifier, boundary.ToString("O", CultureInfo.InvariantCulture));
            result.IsValid.Should().BeTrue();
        }

        foreach (var outside in new[] { FixedNow - tolerance - TimeSpan.FromTicks(1), FixedNow + tolerance + TimeSpan.FromTicks(1) })
        {
            var result = await VerifyTimestampAsync(verifier, outside.ToString("O", CultureInfo.InvariantCulture));
            result.IsValid.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("999999999999999999")]
    [InlineData("-999999999999999999")]
    public async Task TimestampVerifier_OutOfRangeNumericTimestamp_ReturnsSafeRejection(string timestamp)
    {
        var (verifier, _) = CreateTimestampVerifier();
        WebhookVerificationResult result = default;
        Func<Task> act = async () => result = await VerifyTimestampAsync(verifier, timestamp);

        await act.Should().NotThrowAsync();
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be("Timestamp format is invalid.");
    }

    [Fact]
    public async Task TimestampVerifier_MissingHeader_RequiresExplicitOptOut()
    {
        var (strictVerifier, _) = CreateTimestampVerifier();
        var (optedOutVerifier, _) = CreateTimestampVerifier(timestamp => timestamp.AllowMissing = true);
        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>()
        };

        var strictResult = await strictVerifier.VerifyAsync(context);
        var optedOutResult = await optedOutVerifier.VerifyAsync(context);

        strictResult.IsValid.Should().BeFalse();
        optedOutResult.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task TimestampVerifier_MalformedHeader_IsRejectedEvenWhenMissingIsAllowed()
    {
        var (verifier, _) = CreateTimestampVerifier(timestamp => timestamp.AllowMissing = true);

        var result = await VerifyTimestampAsync(verifier, "not-a-timestamp");

        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be("Timestamp format is invalid.");
    }

    [Fact]
    public async Task TimestampVerifier_CancelledToken_PropagatesCancellation()
    {
        var (verifier, _) = CreateTimestampVerifier();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>
            {
                [TimestampHeader] = [FixedNow.ToString("O", CultureInfo.InvariantCulture)]
            }
        };

        Func<Task> act = async () => await verifier.VerifyAsync(context, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("verifier", WebhookIngestionStatus.Rejected, "signature-verification-failed")]
    [InlineData("parser", WebhookIngestionStatus.Failed, "event-id-extraction-failed")]
    public async Task Ingestion_VerifierReasonAndParserDetail_AreAbsentFromSafeResultLogAndActivitySurfaces(
        string failureStage,
        WebhookIngestionStatus expectedStatus,
        string expectedCode)
    {
        var logs = new WebhookLoggingTests.CapturingLoggerProvider();
        var activities = new ConcurrentQueue<Activity>();
        using var listener = StartActivityListener(activities);
        var services = new ServiceCollection();
        services.AddWebhookKit(options =>
        {
            options.AddProvider(ProviderName, provider =>
            {
                provider.Signature.HeaderName = SignatureHeader;
                provider.Signature.Secret = ConfiguredSecretMarker;
                provider.Signature.AdditionalSecrets.Add(RotationSecretMarker);
                provider.Timestamp.AllowMissing = true;
                provider.EventIdHeaderName = EventIdHeader;
                provider.EventTypeHeaderName = EventTypeHeader;
            });
        });
        services.AddSingleton<IWebhookClock>(new FakeWebhookClock(FixedNow));
        services.AddSingleton<IWebhookSignatureVerifier>(failureStage == "verifier"
            ? new MarkerRejectingSignatureVerifier()
            : new AcceptingSignatureVerifier());
        services.AddSingleton<IWebhookEventIdExtractor, MarkerThrowingEventIdExtractor>();
        services.AddSingleton<ILogger<WebhookIngestionService>>(logs.CreateLogger<WebhookIngestionService>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        byte[] body = Encoding.UTF8.GetBytes($"{{\"raw\":\"{BodyMarker}\",\"value\":42}}");
        var request = new WebhookIngestionRequest
        {
            WebhookId = $"security-{failureStage}-webhook",
            Provider = ProviderName,
            HttpMethod = "POST",
            RequestPath = "/webhooks/security",
            Headers = new Dictionary<string, string[]>
            {
                [EventIdHeader] = [$"security-{failureStage}-event"],
                [EventTypeHeader] = ["security.event"],
                ["Authorization"] = [HeaderMarker],
                ["X-Provider-Signature"] = [SignatureMarker]
            },
            RawBody = body,
            ContentType = "application/json",
            ContentLength = body.Length
        };

        var result = await service.IngestAsync(request);

        result.Status.Should().Be(expectedStatus);
        result.FailureCode.Should().Be(expectedCode);
        result.ToString().Should().Be($"{expectedStatus}:{expectedCode}");
        if (failureStage == "verifier")
        {
            result.FailureReason.Should().Be(VerifierReasonMarker);
        }
        else
        {
            result.FailureReason.Should().BeNull();
        }

        activities.Should().NotBeEmpty();
        logs.Entries.Should().NotBeEmpty();
        logs.Entries.Should().OnlyContain(entry => entry.Exception == null);
        (await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetAsync(ProviderName, $"security-{failureStage}-event")).Should().BeNull();
        var safeSurfaces = $"{result}|{string.Join("|", logs.Entries.SelectMany(entry => new[]
        {
            entry.Template,
            entry.Formatted,
            string.Join("|", entry.State.Select(pair => $"{pair.Key}={pair.Value}"))
        }))}|{string.Join("|", activities.SelectMany(activity => new[]
        {
            activity.DisplayName,
            activity.ToString(),
            string.Join("|", activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")),
            string.Join("|", activity.Events.SelectMany(activityEvent => activityEvent.Tags.Select(tag => $"{tag.Key}={tag.Value}")))
        }))}";
        AssertNoMarkers(safeSurfaces, BodyMarker, HeaderMarker, SignatureMarker, ConfiguredSecretMarker, RotationSecretMarker, VerifierReasonMarker, ParserDetailMarker, HandlerMessageMarker, StackTextMarker);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task Deduplication_SharedBarrierAtEachScale_AllowsOneWinnerAndOneRecord(int callerCount)
    {
        var store = new Core.Stores.InMemoryWebhookStore(new FakeWebhookClock(FixedNow));
        var deduplicator = new Core.Deduplication.DefaultWebhookDeduplicator(store);
        var barrier = new SharedStartBarrier(callerCount);
        var tasks = Enumerable.Range(0, callerCount)
            .Select(async index =>
            {
                await barrier.SignalAndWaitAsync();
                return await deduplicator.TryAcquireAsync(CreateRecord($"security-webhook-{index:D4}"));
            })
            .ToArray();

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        results.Count(result => result).Should().Be(1);
        (await store.GetAsync(ProviderName, "security-event")).Should().NotBeNull();
        (await store.GetRecoverableAsync(FixedNow, TimeSpan.Zero, callerCount + 1)).Should().ContainSingle();
    }

    [Fact]
    public async Task Ingestion_FailureSurfaces_DoNotExposeInputSecretReasonParserHandlerOrStackMarkers()
    {
        var logs = new WebhookLoggingTests.CapturingLoggerProvider();
        var processor = new MarkerThrowingDispatchProcessor();
        var activities = new ConcurrentQueue<Activity>();
        using var listener = StartActivityListener(activities);
        var services = new ServiceCollection();
        services.AddWebhookKit(options =>
        {
            options.AddProvider(ProviderName, provider =>
            {
                provider.Signature.HeaderName = SignatureHeader;
                provider.Signature.Secret = ConfiguredSecretMarker;
                provider.Signature.AdditionalSecrets.Add(RotationSecretMarker);
                provider.Timestamp.HeaderName = TimestampHeader;
                provider.EventIdHeaderName = EventIdHeader;
                provider.EventTypeHeaderName = EventTypeHeader;
            });
        });
        services.AddSingleton<IWebhookClock>(new FakeWebhookClock(FixedNow));
        services.AddSingleton<IWebhookDispatchProcessor>(processor);
        services.AddSingleton<ILogger<WebhookIngestionService>>(logs.CreateLogger<WebhookIngestionService>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        byte[] body = Encoding.UTF8.GetBytes($"{{\"raw\":\"{BodyMarker}\",\"value\":42}}");
        var signature = new WebhookSignatureGenerator(ConfiguredSecretMarker).Generate(body);
        var request = new WebhookIngestionRequest
        {
            WebhookId = "security-core-webhook",
            CorrelationId = "security-core-correlation",
            Provider = ProviderName,
            HttpMethod = "POST",
            RequestPath = "/webhooks/security",
            Headers = new Dictionary<string, string[]>
            {
                [EventIdHeader] = ["security-core-event"],
                [EventTypeHeader] = ["security.event"],
                [TimestampHeader] = [FixedNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)],
                ["Authorization"] = [HeaderMarker],
                ["X-Provider-Signature"] = [SignatureMarker],
                [SignatureHeader] = [signature]
            },
            RawBody = body,
            ContentType = "application/json",
            ContentLength = body.Length
        };

        var result = await service.IngestAsync(request);

        result.Status.Should().Be(WebhookIngestionStatus.Failed);
        result.FailureCode.Should().Be("handler-failed");
        processor.Exception.Should().NotBeNull();
        processor.Exception!.ToString().Should().Contain(HandlerMessageMarker);
        processor.Exception.ToString().Should().Contain(ParserDetailMarker);
        processor.Exception.ToString().Should().Contain(StackTextMarker);
        result.ToString().Should().Be("Failed:handler-failed");
        var stored = await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetAsync(ProviderName, "security-core-event");
        stored.Should().NotBeNull();
        stored!.FailureReason.Should().Be("handler-failed");
        stored.FailureCode.Should().Be("handler-failed");
        var logText = string.Join("|", logs.Entries.SelectMany(entry => new[]
        {
            entry.Template,
            entry.Formatted,
            string.Join("|", entry.State.Select(pair => $"{pair.Key}={pair.Value}")),
            entry.Exception?.ToString() ?? string.Empty
        }));
        var activityText = string.Join("|", activities.SelectMany(activity => new[]
        {
            activity.DisplayName,
            activity.ToString(),
            string.Join("|", activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")),
            string.Join("|", activity.Events.SelectMany(activityEvent => activityEvent.Tags.Select(tag => $"{tag.Key}={tag.Value}")))
        }));
        var safeSurfaces = $"{result}|{result.FailureCode}|{stored.FailureReason}|{stored.FailureCode}|{logText}|{activityText}";
        logs.Entries.Should().OnlyContain(entry => entry.Exception == null);
        activities.Should().NotBeEmpty();
        AssertNoMarkers(safeSurfaces, BodyMarker, HeaderMarker, SignatureMarker, ConfiguredSecretMarker, RotationSecretMarker, VerifierReasonMarker, ParserDetailMarker, HandlerMessageMarker, StackTextMarker);
    }

    private static string CreateFailureSignature(byte[] body, WebhookHashAlgorithm algorithm, string failureMode)
    {
        return failureMode switch
        {
            "tampered" => TamperHex(ComputeSignature(body, ConfiguredSecretMarker, algorithm, WebhookSignatureEncoding.Hex)),
            "malformed" => SignatureMarker,
            "wrong-secret" => ComputeSignature(body, "security-unknown-secret", algorithm, WebhookSignatureEncoding.Hex),
            "encoding-mismatch" => ComputeSignature(body, ConfiguredSecretMarker, algorithm, WebhookSignatureEncoding.Base64),
            _ => throw new ArgumentOutOfRangeException(nameof(failureMode))
        };
    }

    private static string ComputeSignature(
        byte[] body,
        string secret,
        WebhookHashAlgorithm algorithm,
        WebhookSignatureEncoding encoding)
    {
        byte[] hash = algorithm == WebhookHashAlgorithm.HmacSha256
            ? HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)
            : HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), body);
        return encoding == WebhookSignatureEncoding.Hex
            ? Convert.ToHexString(hash).ToLowerInvariant()
            : Convert.ToBase64String(hash);
    }

    private static string TamperHex(string signature)
    {
        return (signature[0] == '0' ? '1' : '0') + signature[1..];
    }

    private static (WebhookTimestampVerifier Verifier, FakeWebhookClock Clock) CreateTimestampVerifier(
        Action<WebhookTimestampOptions>? configure = null)
    {
        var clock = new FakeWebhookClock(FixedNow);
        var options = new WebhookKitOptions();
        options.AddProvider(ProviderName, provider =>
        {
            provider.Signature.HeaderName = SignatureHeader;
            provider.Signature.Secret = ConfiguredSecretMarker;
            provider.Timestamp.HeaderName = TimestampHeader;
            provider.Timestamp.Tolerance = TimeSpan.FromMinutes(5);
            provider.Timestamp.AllowMissing = false;
            configure?.Invoke(provider.Timestamp);
        });
        return (new WebhookTimestampVerifier(Microsoft.Extensions.Options.Options.Create(options), clock), clock);
    }

    private static ValueTask<WebhookVerificationResult> VerifyTimestampAsync(
        WebhookTimestampVerifier verifier,
        string timestamp)
    {
        return verifier.VerifyAsync(new WebhookVerificationContext
        {
            Provider = ProviderName,
            RawBody = [],
            Headers = new Dictionary<string, string[]>
            {
                [TimestampHeader] = [timestamp]
            }
        });
    }

    private static WebhookRecord CreateRecord(string id)
    {
        return new WebhookRecord
        {
            Id = id,
            Provider = ProviderName,
            EventId = "security-event",
            DeduplicationKey = $"{ProviderName}:security-event",
            HttpMethod = "POST",
            RequestPath = "/webhooks/security",
            Headers = new Dictionary<string, string[]>(),
            RawBody = [1, 2, 3],
            ReceivedAt = FixedNow
        };
    }

    private static ActivityListener StartActivityListener(ConcurrentQueue<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "WebhookKit" && source.Version == "1.0.0",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("webhook.provider"), ProviderName))
                {
                    activities.Enqueue(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static void AssertNoMarkers(string text, params string[] markers)
    {
        foreach (var marker in markers)
        {
            text.Should().NotContain(marker);
        }
    }

    private sealed class AcceptingSignatureVerifier : IWebhookSignatureVerifier
    {
        public ValueTask<WebhookVerificationResult> VerifyAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(WebhookVerificationResult.Success());
        }
    }

    private sealed class MarkerRejectingSignatureVerifier : IWebhookSignatureVerifier
    {
        public ValueTask<WebhookVerificationResult> VerifyAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(WebhookVerificationResult.Fail(VerifierReasonMarker));
        }
    }

    private sealed class MarkerThrowingEventIdExtractor : IWebhookEventIdExtractor
    {
        public ValueTask<string?> ExtractAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new MarkedSecurityException($"{ParserDetailMarker}|{StackTextMarker}");
        }
    }

    private sealed class SharedStartBarrier(int participantCount)
    {
        private readonly TaskCompletionSource<bool> _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task SignalAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrived) == participantCount)
            {
                _released.TrySetResult(true);
            }

            await _released.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class MarkerThrowingDispatchProcessor : IWebhookDispatchProcessor
    {
        public Exception? Exception { get; private set; }

        public Task<WebhookDispatchResult> DispatchAsync(
            WebhookContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception = new MarkedSecurityException(
                $"{HandlerMessageMarker}|{ParserDetailMarker}|{VerifierReasonMarker}");
            throw Exception;
        }
    }

    private sealed class MarkedSecurityException(string message) : Exception(message)
    {
        public override string? StackTrace => $"at {StackTextMarker}(SecurityBoundary)";
    }

    public sealed record SecurityPayload(int Value);
}
