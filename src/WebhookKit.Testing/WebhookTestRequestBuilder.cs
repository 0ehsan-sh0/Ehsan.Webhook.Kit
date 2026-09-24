using System.Globalization;
using System.Text.Json;
using WebhookKit.Abstractions;
using WebhookKit.Core.Clocks;
using WebhookKit.Core.Options;

namespace WebhookKit.Testing;

public sealed class WebhookTestRequestBuilder
{
    public const string DefaultProviderHeaderName = "X-Webhook-Provider";
    public const string DefaultEventIdHeaderName = "X-Webhook-Event-Id";
    public const string DefaultEventTypeHeaderName = "X-Webhook-Event-Type";
    public const string DefaultTimestampHeaderName = "X-Webhook-Timestamp";
    public const string DefaultSignatureHeaderName = "X-Webhook-Signature";
    public const string DefaultContentType = "application/json";
    public const string DefaultPath = "/webhooks";

    private readonly IWebhookClock _clock;
    private readonly string? _provider;
    private readonly string? _eventId;
    private readonly string? _eventType;
    private readonly string? _timestampText;
    private readonly Dictionary<string, List<string>> _headers;
    private readonly byte[] _rawBody;
    private readonly JsonSerializerOptions? _jsonSerializerOptions;
    private readonly string? _contentType;
    private readonly bool _contentTypeWasExplicit;
    private readonly string _requestUri;
    private readonly HttpMethod _method;
    private readonly string _providerHeaderName;
    private readonly string _eventIdHeaderName;
    private readonly string _eventTypeHeaderName;
    private readonly string _timestampHeaderName;
    private readonly string _signatureHeaderName;

    public WebhookTestRequestBuilder(IWebhookClock? clock = null)
        : this(
            clock ?? new SystemWebhookClock(),
            null,
            null,
            null,
            null,
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<byte>(),
            null,
            null,
            false,
            DefaultPath,
            HttpMethod.Post,
            DefaultProviderHeaderName,
            DefaultEventIdHeaderName,
            DefaultEventTypeHeaderName,
            DefaultTimestampHeaderName,
            DefaultSignatureHeaderName)
    {
    }

    private WebhookTestRequestBuilder(
        IWebhookClock clock,
        string? provider,
        string? eventId,
        string? eventType,
        string? timestampText,
        Dictionary<string, List<string>> headers,
        byte[] rawBody,
        JsonSerializerOptions? jsonSerializerOptions,
        string? contentType,
        bool contentTypeWasExplicit,
        string requestUri,
        HttpMethod method,
        string providerHeaderName,
        string eventIdHeaderName,
        string eventTypeHeaderName,
        string timestampHeaderName,
        string signatureHeaderName)
    {
        _clock = clock;
        _provider = provider;
        _eventId = eventId;
        _eventType = eventType;
        _timestampText = timestampText;
        _headers = headers;
        _rawBody = rawBody;
        _jsonSerializerOptions = jsonSerializerOptions;
        _contentType = contentType;
        _contentTypeWasExplicit = contentTypeWasExplicit;
        _requestUri = requestUri;
        _method = method;
        _providerHeaderName = providerHeaderName;
        _eventIdHeaderName = eventIdHeaderName;
        _eventTypeHeaderName = eventTypeHeaderName;
        _timestampHeaderName = timestampHeaderName;
        _signatureHeaderName = signatureHeaderName;
    }

    public static WebhookTestRequestBuilder Create(string provider, IWebhookClock? clock = null)
    {
        return new WebhookTestRequestBuilder(clock).WithProvider(provider);
    }

    public static WebhookTestRequestBuilder Create(
        string provider,
        string? eventId,
        string? eventType,
        IWebhookClock? clock = null)
    {
        return new WebhookTestRequestBuilder(clock)
            .WithProvider(provider)
            .WithEventId(eventId)
            .WithEventType(eventType);
    }

    public string? Provider => _provider;
    public string? EventId => _eventId;
    public string? EventType => _eventType;
    public string? Timestamp => _timestampText;
    public string? TimestampText => _timestampText;
    public string Path => _requestUri;
    public string RequestUri => _requestUri;
    public HttpMethod Method => _method;
    public string? ContentType => _contentType;
    public JsonSerializerOptions? JsonSerializerOptions => CloneOptions(_jsonSerializerOptions);
    public byte[] RawBody => _rawBody.ToArray();
    public ReadOnlyMemory<byte> RawBodyMemory => _rawBody.ToArray();
    public IReadOnlyDictionary<string, string[]> Headers => _headers.ToDictionary(
        pair => pair.Key,
        pair => pair.Value.ToArray(),
        StringComparer.OrdinalIgnoreCase);

    public WebhookTestRequestBuilder WithClock(IWebhookClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return Copy(clock: clock);
    }

    public WebhookTestRequestBuilder WithProvider(string provider)
    {
        ValidateRequiredText(provider, nameof(provider));
        return Copy(provider: provider);
    }

    public WebhookTestRequestBuilder WithEventId(string? eventId)
    {
        return Copy(eventId: eventId, replaceEventId: true);
    }

    public WebhookTestRequestBuilder WithEventType(string? eventType)
    {
        return Copy(eventType: eventType, replaceEventType: true);
    }

    public WebhookTestRequestBuilder WithTimestamp(string timestamp)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        return Copy(timestampText: timestamp);
    }

    public WebhookTestRequestBuilder WithTimestamp(DateTimeOffset timestamp)
    {
        return WithTimestamp(timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
    }

    public WebhookTestRequestBuilder WithTimestamp(long unixSeconds)
    {
        return WithTimestamp(unixSeconds.ToString(CultureInfo.InvariantCulture));
    }

    public WebhookTestRequestBuilder WithTimestamp()
    {
        return WithTimestamp(_clock.UtcNow);
    }

    public WebhookTestRequestBuilder WithHeader(string name, string value)
    {
        ValidateHeaderName(name);
        ArgumentNullException.ThrowIfNull(value);
        return WithHeaderValues(name, value);
    }

    public WebhookTestRequestBuilder WithHeaderValues(string name, params string[] values)
    {
        ValidateHeaderName(name);
        ArgumentNullException.ThrowIfNull(values);

        var nextHeaders = CloneHeaders(_headers);
        if (!nextHeaders.TryGetValue(name, out var nextValues))
        {
            nextValues = [];
            nextHeaders.Add(name, nextValues);
        }

        nextValues.AddRange(values);
        return Copy(headers: nextHeaders);
    }

    public WebhookTestRequestBuilder WithHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var nextHeaders = CloneHeaders(_headers);
        foreach (var header in headers)
        {
            ValidateHeaderName(header.Key);
            ArgumentNullException.ThrowIfNull(header.Value);
            if (!nextHeaders.TryGetValue(header.Key, out var values))
            {
                values = [];
                nextHeaders.Add(header.Key, values);
            }

            values.Add(header.Value);
        }

        return Copy(headers: nextHeaders);
    }

    public WebhookTestRequestBuilder WithJson<T>(T payload, JsonSerializerOptions? serializerOptions = null)
    {
        var options = CloneOptions(serializerOptions ?? _jsonSerializerOptions);
        var body = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            payload?.GetType() ?? typeof(object),
            options ?? new JsonSerializerOptions());
        var nextContentType = _contentType;
        var nextExplicitContentType = _contentTypeWasExplicit;
        if (nextContentType is null && !nextExplicitContentType)
        {
            nextContentType = DefaultContentType;
        }

        return Copy(
            rawBody: body,
            jsonSerializerOptions: options,
            contentType: nextContentType,
            contentTypeWasExplicit: nextExplicitContentType);
    }

    public WebhookTestRequestBuilder WithPayload<T>(T payload, JsonSerializerOptions? serializerOptions = null)
    {
        return WithJson(payload, serializerOptions);
    }

    public WebhookTestRequestBuilder WithRawBody(ReadOnlySpan<byte> rawBody)
    {
        var nextContentType = _contentType;
        var nextExplicitContentType = _contentTypeWasExplicit;
        if (!nextExplicitContentType && string.Equals(nextContentType, DefaultContentType, StringComparison.OrdinalIgnoreCase))
        {
            nextContentType = null;
        }

        return Copy(
            rawBody: rawBody.ToArray(),
            jsonSerializerOptions: null,
            replaceJsonSerializerOptions: true,
            contentType: nextContentType,
            contentTypeWasExplicit: nextExplicitContentType);
    }

    public WebhookTestRequestBuilder WithRawBody(byte[] rawBody)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        return WithRawBody(rawBody.AsSpan());
    }

    public WebhookTestRequestBuilder WithBody(ReadOnlySpan<byte> rawBody)
    {
        return WithRawBody(rawBody);
    }

    public WebhookTestRequestBuilder WithContentType(string contentType)
    {
        ValidateRequiredText(contentType, nameof(contentType));
        return Copy(contentType: contentType, contentTypeWasExplicit: true);
    }

    public WebhookTestRequestBuilder WithJsonSerializerOptions(JsonSerializerOptions? serializerOptions)
    {
        return Copy(
            jsonSerializerOptions: CloneOptions(serializerOptions),
            replaceJsonSerializerOptions: true);
    }

    public WebhookTestRequestBuilder WithPath(string path)
    {
        ValidateRequiredText(path, nameof(path));
        return Copy(requestUri: path);
    }

    public WebhookTestRequestBuilder WithUri(string requestUri)
    {
        return WithPath(requestUri);
    }

    public WebhookTestRequestBuilder WithUri(Uri requestUri)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        return WithPath(requestUri.OriginalString);
    }

    public WebhookTestRequestBuilder WithMethod(HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return Copy(method: new HttpMethod(method.Method));
    }

    public WebhookTestRequestBuilder WithProviderHeaderName(string headerName)
    {
        ValidateRequiredText(headerName, nameof(headerName));
        return Copy(providerHeaderName: headerName);
    }

    public WebhookTestRequestBuilder WithEventIdHeaderName(string headerName)
    {
        ValidateRequiredText(headerName, nameof(headerName));
        return Copy(eventIdHeaderName: headerName);
    }

    public WebhookTestRequestBuilder WithEventTypeHeaderName(string headerName)
    {
        ValidateRequiredText(headerName, nameof(headerName));
        return Copy(eventTypeHeaderName: headerName);
    }

    public WebhookTestRequestBuilder WithTimestampHeaderName(string headerName)
    {
        ValidateRequiredText(headerName, nameof(headerName));
        return Copy(timestampHeaderName: headerName);
    }

    public WebhookTestRequestBuilder WithSignatureHeaderName(string headerName)
    {
        ValidateRequiredText(headerName, nameof(headerName));
        return Copy(signatureHeaderName: headerName);
    }

    public WebhookTestRequestBuilder WithHeaderNames(
        string providerHeaderName,
        string eventIdHeaderName,
        string eventTypeHeaderName,
        string timestampHeaderName,
        string? signatureHeaderName = null)
    {
        ValidateRequiredText(providerHeaderName, nameof(providerHeaderName));
        ValidateRequiredText(eventIdHeaderName, nameof(eventIdHeaderName));
        ValidateRequiredText(eventTypeHeaderName, nameof(eventTypeHeaderName));
        ValidateRequiredText(timestampHeaderName, nameof(timestampHeaderName));
        if (signatureHeaderName is not null)
        {
            ValidateRequiredText(signatureHeaderName, nameof(signatureHeaderName));
        }

        return Copy(
            providerHeaderName: providerHeaderName,
            eventIdHeaderName: eventIdHeaderName,
            eventTypeHeaderName: eventTypeHeaderName,
            timestampHeaderName: timestampHeaderName,
            signatureHeaderName: signatureHeaderName ?? _signatureHeaderName);
    }

    public WebhookTestRequestBuilder WithSignature(string signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        return WithHeaderValues(_signatureHeaderName, signature);
    }

    public HttpRequestMessage Build()
    {
        var timestamp = ResolveTimestamp();
        return BuildCore(timestamp, _rawBody.ToArray(), null, _signatureHeaderName);
    }

    public HttpRequestMessage BuildSigned(WebhookSignatureGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return BuildSignedCore(generator, null, null, _signatureHeaderName);
    }

    public HttpRequestMessage BuildSigned(
        WebhookSignatureGenerator generator,
        Func<ReadOnlyMemory<byte>, string?, ReadOnlyMemory<byte>> customSigningInput)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(customSigningInput);
        return BuildSignedCore(generator, customSigningInput, null, _signatureHeaderName);
    }

    public HttpRequestMessage BuildSigned(
        WebhookSignatureGenerator generator,
        string signatureHeaderName)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ValidateRequiredText(signatureHeaderName, nameof(signatureHeaderName));
        return BuildSignedCore(generator, null, null, signatureHeaderName);
    }

    public HttpRequestMessage BuildSigned(
        WebhookSignatureGenerator generator,
        Func<ReadOnlyMemory<byte>, string?, ReadOnlyMemory<byte>> customSigningInput,
        string signatureHeaderName)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(customSigningInput);
        ValidateRequiredText(signatureHeaderName, nameof(signatureHeaderName));
        return BuildSignedCore(generator, customSigningInput, null, signatureHeaderName);
    }

    public HttpRequestMessage BuildSigned(
        string secret,
        WebhookHashAlgorithm algorithm = WebhookHashAlgorithm.HmacSha256,
        WebhookSignatureEncoding encoding = WebhookSignatureEncoding.Hex,
        WebhookSignatureInput input = WebhookSignatureInput.RawBody,
        string? timestampSeparator = null,
        string? signatureHeaderName = null,
        IEnumerable<string>? rotationSecrets = null)
    {
        var generator = new WebhookSignatureGenerator(
            secret,
            algorithm,
            encoding,
            input,
            timestampSeparator,
            rotationSecrets);
        return BuildSigned(generator, signatureHeaderName ?? _signatureHeaderName);
    }

    public HttpRequestMessage Sign(WebhookSignatureGenerator generator)
    {
        return BuildSigned(generator);
    }

    public HttpRequestMessage Sign(
        string secret,
        WebhookHashAlgorithm algorithm = WebhookHashAlgorithm.HmacSha256,
        WebhookSignatureEncoding encoding = WebhookSignatureEncoding.Hex,
        WebhookSignatureInput input = WebhookSignatureInput.RawBody,
        string? timestampSeparator = null,
        string? signatureHeaderName = null,
        IEnumerable<string>? rotationSecrets = null)
    {
        return BuildSigned(secret, algorithm, encoding, input, timestampSeparator, signatureHeaderName, rotationSecrets);
    }

    public override string ToString()
    {
        return nameof(WebhookTestRequestBuilder);
    }

    private HttpRequestMessage BuildSignedCore(
        WebhookSignatureGenerator generator,
        Func<ReadOnlyMemory<byte>, string?, ReadOnlyMemory<byte>>? customSigningInput,
        byte[]? body,
        string signatureHeaderName)
    {
        var timestamp = ResolveTimestamp();
        var exactBody = body ?? _rawBody.ToArray();
        var signature = customSigningInput is null
            ? generator.Generate(exactBody, timestamp)
            : generator.GenerateWithCustomInput(exactBody, timestamp, customSigningInput);
        return BuildCore(timestamp, exactBody, signature, signatureHeaderName);
    }

    private HttpRequestMessage BuildCore(
        string timestamp,
        byte[] body,
        string? signature,
        string signatureHeaderName)
    {
        if (string.IsNullOrWhiteSpace(_provider))
        {
            throw new InvalidOperationException("A provider is required before building a request.");
        }

        var request = new HttpRequestMessage(_method, _requestUri)
        {
            Content = new ByteArrayContent(body)
        };

        foreach (var header in _headers)
        {
            AddHeader(request, header.Key, header.Value);
        }

        AddHeader(request, _providerHeaderName, [_provider]);
        if (_eventId is not null)
        {
            AddHeader(request, _eventIdHeaderName, [_eventId]);
        }

        if (_eventType is not null)
        {
            AddHeader(request, _eventTypeHeaderName, [_eventType]);
        }

        AddHeader(request, _timestampHeaderName, [timestamp]);
        if (signature is not null)
        {
            AddHeader(request, signatureHeaderName, [signature]);
        }

        if (_contentType is not null)
        {
            request.Content.Headers.TryAddWithoutValidation("Content-Type", _contentType);
        }

        return request;
    }

    private string ResolveTimestamp()
    {
        return _timestampText ?? _clock.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    }

    private WebhookTestRequestBuilder Copy(
        IWebhookClock? clock = null,
        string? provider = null,
        string? eventId = null,
        bool replaceEventId = false,
        string? eventType = null,
        bool replaceEventType = false,
        string? timestampText = null,
        Dictionary<string, List<string>>? headers = null,
        byte[]? rawBody = null,
        JsonSerializerOptions? jsonSerializerOptions = null,
        bool replaceJsonSerializerOptions = false,
        string? contentType = null,
        bool? contentTypeWasExplicit = null,
        string? requestUri = null,
        HttpMethod? method = null,
        string? providerHeaderName = null,
        string? eventIdHeaderName = null,
        string? eventTypeHeaderName = null,
        string? timestampHeaderName = null,
        string? signatureHeaderName = null)
    {
        return new WebhookTestRequestBuilder(
            clock ?? _clock,
            provider ?? _provider,
            replaceEventId ? eventId : _eventId,
            replaceEventType ? eventType : _eventType,
            timestampText ?? _timestampText,
            headers ?? CloneHeaders(_headers),
            rawBody?.ToArray() ?? _rawBody.ToArray(),
            replaceJsonSerializerOptions
                ? jsonSerializerOptions
                : jsonSerializerOptions ?? CloneOptions(_jsonSerializerOptions),
            contentType ?? _contentType,
            contentTypeWasExplicit ?? _contentTypeWasExplicit,
            requestUri ?? _requestUri,
            method is null ? new HttpMethod(_method.Method) : new HttpMethod(method.Method),
            providerHeaderName ?? _providerHeaderName,
            eventIdHeaderName ?? _eventIdHeaderName,
            eventTypeHeaderName ?? _eventTypeHeaderName,
            timestampHeaderName ?? _timestampHeaderName,
            signatureHeaderName ?? _signatureHeaderName);
    }

    private static Dictionary<string, List<string>> CloneHeaders(Dictionary<string, List<string>> source)
    {
        return source.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static JsonSerializerOptions? CloneOptions(JsonSerializerOptions? options)
    {
        return options is null ? null : new JsonSerializerOptions(options);
    }

    private static void AddHeader(HttpRequestMessage request, string name, IEnumerable<string> values)
    {
        if (IsContentHeader(name))
        {
            foreach (var value in values)
            {
                request.Content!.Headers.TryAddWithoutValidation(name, value);
            }

            return;
        }

        foreach (var value in values)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static bool IsContentHeader(string name)
    {
        return name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateHeaderName(string name)
    {
        ValidateRequiredText(name, nameof(name));
    }

    private static void ValidateRequiredText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }
    }
}
