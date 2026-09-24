using System.Globalization;
using System.Text.Json;
using WebhookKit.Abstractions;
using WebhookKit.Core.Clocks;
using WebhookKit.Core.Options;

namespace WebhookKit.Testing;

/// <summary>Immutable fluent builder for constructing signed webhook test requests.</summary>
/// <remarks>Each mutation returns a new builder; raw body properties return copies and callers own returned requests and clients.</remarks>
public sealed class WebhookTestRequestBuilder
{
    /// <summary>Default provider metadata header name.</summary>
    public const string DefaultProviderHeaderName = "X-Webhook-Provider";
    /// <summary>Default event ID header name.</summary>
    public const string DefaultEventIdHeaderName = "X-Webhook-Event-Id";
    /// <summary>Default event type header name.</summary>
    public const string DefaultEventTypeHeaderName = "X-Webhook-Event-Type";
    /// <summary>Default timestamp header name.</summary>
    public const string DefaultTimestampHeaderName = "X-Webhook-Timestamp";
    /// <summary>Default signature header name.</summary>
    public const string DefaultSignatureHeaderName = "X-Webhook-Signature";
    /// <summary>Default JSON content type.</summary>
    public const string DefaultContentType = "application/json";
    /// <summary>Default request path.</summary>
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

    /// <summary>Creates a builder with default metadata and an optional deterministic clock.</summary>
    /// <param name="clock">Clock used when a timestamp is generated at build time; the system clock is used when omitted.</param>
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

    /// <summary>Creates a builder for a provider.</summary>
    /// <param name="provider">Provider metadata value.</param>
    /// <param name="clock">Optional clock used for generated timestamps.</param>
    /// <returns>A new builder.</returns>
    public static WebhookTestRequestBuilder Create(string provider, IWebhookClock? clock = null)
    {
        return new WebhookTestRequestBuilder(clock).WithProvider(provider);
    }

    /// <summary>Creates a builder with provider, event ID, and event type metadata.</summary>
    /// <param name="provider">Provider metadata value.</param>
    /// <param name="eventId">Optional provider event ID.</param>
    /// <param name="eventType">Optional provider event type.</param>
    /// <param name="clock">Optional clock used for generated timestamps.</param>
    /// <returns>A new builder.</returns>
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

    /// <summary>Configured provider metadata.</summary>
    public string? Provider => _provider;
    /// <summary>Configured provider event ID.</summary>
    public string? EventId => _eventId;
    /// <summary>Configured provider event type.</summary>
    public string? EventType => _eventType;
    /// <summary>Configured timestamp text, if any.</summary>
    public string? Timestamp => _timestampText;
    /// <summary>Compatibility alias for <see cref="Timestamp"/>.</summary>
    public string? TimestampText => _timestampText;
    /// <summary>Configured request path or URI.</summary>
    public string Path => _requestUri;
    /// <summary>Compatibility alias for <see cref="Path"/>.</summary>
    public string RequestUri => _requestUri;
    /// <summary>Configured HTTP method.</summary>
    public HttpMethod Method => _method;
    /// <summary>Configured content type.</summary>
    public string? ContentType => _contentType;
    /// <summary>Copy of the configured JSON serializer options.</summary>
    public JsonSerializerOptions? JsonSerializerOptions => CloneOptions(_jsonSerializerOptions);
    /// <summary>Copy of the exact body bytes.</summary>
    public byte[] RawBody => _rawBody.ToArray();
    /// <summary>Copy of the exact body bytes as read-only memory.</summary>
    public ReadOnlyMemory<byte> RawBodyMemory => _rawBody.ToArray();
    /// <summary>Snapshot of configured header values.</summary>
    public IReadOnlyDictionary<string, string[]> Headers => _headers.ToDictionary(
        pair => pair.Key,
        pair => pair.Value.ToArray(),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns a copy using the supplied clock for generated timestamps.</summary>
    /// <param name="clock">Clock to use in the new builder.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithClock(IWebhookClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return Copy(clock: clock);
    }

    /// <summary>Returns a copy with provider metadata.</summary>
    /// <param name="provider">Non-empty provider value.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithProvider(string provider)
    {
        ValidateRequiredText(provider, nameof(provider));
        return Copy(provider: provider);
    }

    /// <summary>Returns a copy with an event ID, including <see langword="null"/> to clear it.</summary>
    /// <param name="eventId">Optional event ID.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithEventId(string? eventId)
    {
        return Copy(eventId: eventId, replaceEventId: true);
    }

    /// <summary>Returns a copy with an event type, including <see langword="null"/> to clear it.</summary>
    /// <param name="eventType">Optional event type.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithEventType(string? eventType)
    {
        return Copy(eventType: eventType, replaceEventType: true);
    }

    /// <summary>Returns a copy with explicit timestamp text.</summary>
    /// <param name="timestamp">Timestamp text to send.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithTimestamp(string timestamp)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        return Copy(timestampText: timestamp);
    }

    /// <summary>Returns a copy with a Unix-seconds timestamp.</summary>
    /// <param name="timestamp">Timestamp to encode as Unix seconds.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithTimestamp(DateTimeOffset timestamp)
    {
        return WithTimestamp(timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Returns a copy with a Unix-seconds timestamp.</summary>
    /// <param name="unixSeconds">Unix timestamp in seconds.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithTimestamp(long unixSeconds)
    {
        return WithTimestamp(unixSeconds.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Returns a copy whose timestamp is resolved from the builder clock at build time.</summary>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithTimestamp()
    {
        return WithTimestamp(_clock.UtcNow);
    }

    /// <summary>Adds one request header value.</summary>
    /// <param name="name">Header name.</param>
    /// <param name="value">Header value.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithHeader(string name, string value)
    {
        ValidateHeaderName(name);
        ArgumentNullException.ThrowIfNull(value);
        return WithHeaderValues(name, value);
    }

    /// <summary>Adds one or more values to a request header.</summary>
    /// <param name="name">Header name.</param>
    /// <param name="values">Header values to append.</param>
    /// <returns>A new builder.</returns>
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

    /// <summary>Adds a sequence of single-value headers.</summary>
    /// <param name="headers">Header pairs to append.</param>
    /// <returns>A new builder.</returns>
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

    /// <summary>Serializes a payload as UTF-8 JSON.</summary>
    /// <typeparam name="T">Payload type serialized by the test.</typeparam>
    /// <param name="payload">Payload to serialize.</param>
    /// <param name="serializerOptions">Optional serializer settings; a copy is retained.</param>
    /// <returns>A new builder containing the serialized body.</returns>
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

    /// <summary>Compatibility alias for <see cref="WithJson{T}"/>.</summary>
    /// <typeparam name="T">Payload type serialized by the test.</typeparam>
    /// <param name="payload">Payload to serialize.</param>
    /// <param name="serializerOptions">Optional serializer settings.</param>
    /// <returns>A new builder containing the serialized body.</returns>
    public WebhookTestRequestBuilder WithPayload<T>(T payload, JsonSerializerOptions? serializerOptions = null)
    {
        return WithJson(payload, serializerOptions);
    }

    /// <summary>Uses exact caller-supplied body bytes.</summary>
    /// <param name="rawBody">Body bytes copied into the new builder.</param>
    /// <returns>A new builder.</returns>
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

    /// <summary>Uses exact caller-supplied body bytes.</summary>
    /// <param name="rawBody">Body bytes copied into the new builder.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithRawBody(byte[] rawBody)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        return WithRawBody(rawBody.AsSpan());
    }

    /// <summary>Compatibility alias for <see cref="WithRawBody(ReadOnlySpan{byte})"/>.</summary>
    /// <param name="rawBody">Body bytes copied into the new builder.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithBody(ReadOnlySpan<byte> rawBody)
    {
        return WithRawBody(rawBody);
    }

    /// <summary>Sets an explicit content type.</summary>
    /// <param name="contentType">Content type value.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithContentType(string contentType)
    {
        ValidateRequiredText(contentType, nameof(contentType));
        return Copy(contentType: contentType, contentTypeWasExplicit: true);
    }

    /// <summary>Sets the JSON serializer options used by later JSON serialization.</summary>
    /// <param name="serializerOptions">Options to copy, or <see langword="null"/> to clear them.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithJsonSerializerOptions(JsonSerializerOptions? serializerOptions)
    {
        return Copy(
            jsonSerializerOptions: CloneOptions(serializerOptions),
            replaceJsonSerializerOptions: true);
    }

    /// <summary>Sets the request path or URI.</summary>
    /// <param name="path">Non-empty request path.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithPath(string path)
    {
        ValidateRequiredText(path, nameof(path));
        return Copy(requestUri: path);
    }

    /// <summary>Sets the request URI text.</summary>
    /// <param name="requestUri">Non-empty URI text.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithUri(string requestUri)
    {
        return WithPath(requestUri);
    }

    /// <summary>Sets the request URI from a parsed URI.</summary>
    /// <param name="requestUri">URI whose original text is used.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithUri(Uri requestUri)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        return WithPath(requestUri.OriginalString);
    }

    /// <summary>Sets the HTTP method.</summary>
    /// <param name="method">Method copied into the new builder.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithMethod(HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return Copy(method: new HttpMethod(method.Method));
    }

    /// <summary>Sets the provider metadata header name.</summary>
    /// <param name="headerName">Non-empty header name.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithProviderHeaderName(string headerName)
    {
        ValidateRequiredText(headerName, nameof(headerName));
        return Copy(providerHeaderName: headerName);
    }

    /// <summary>Sets the event ID header name.</summary>
    /// <param name="headerName">Non-empty header name.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithEventIdHeaderName(string headerName)
    {
        ValidateRequiredText(headerName, nameof(headerName));
        return Copy(eventIdHeaderName: headerName);
    }

    /// <summary>Sets the event type header name.</summary>
    /// <param name="headerName">Non-empty header name.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithEventTypeHeaderName(string headerName)
    {
        ValidateRequiredText(headerName, nameof(headerName));
        return Copy(eventTypeHeaderName: headerName);
    }

    /// <summary>Sets the timestamp header name.</summary>
    /// <param name="headerName">Non-empty header name.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithTimestampHeaderName(string headerName)
    {
        ValidateRequiredText(headerName, nameof(headerName));
        return Copy(timestampHeaderName: headerName);
    }

    /// <summary>Sets the signature header name.</summary>
    /// <param name="headerName">Non-empty header name.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithSignatureHeaderName(string headerName)
    {
        ValidateRequiredText(headerName, nameof(headerName));
        return Copy(signatureHeaderName: headerName);
    }

    /// <summary>Sets all metadata header names in one operation.</summary>
    /// <param name="providerHeaderName">Provider header name.</param>
    /// <param name="eventIdHeaderName">Event ID header name.</param>
    /// <param name="eventTypeHeaderName">Event type header name.</param>
    /// <param name="timestampHeaderName">Timestamp header name.</param>
    /// <param name="signatureHeaderName">Optional signature header name.</param>
    /// <returns>A new builder.</returns>
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

    /// <summary>Adds a signature value using the configured signature header name.</summary>
    /// <param name="signature">Encoded signature value.</param>
    /// <returns>A new builder.</returns>
    public WebhookTestRequestBuilder WithSignature(string signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        return WithHeaderValues(_signatureHeaderName, signature);
    }

    /// <summary>Builds an unsigned request with a timestamp resolved from the builder.</summary>
    /// <returns>A new request message owned by the caller.</returns>
    public HttpRequestMessage Build()
    {
        var timestamp = ResolveTimestamp();
        return BuildCore(timestamp, _rawBody.ToArray(), null, _signatureHeaderName);
    }

    /// <summary>Builds a request signed by the supplied generator.</summary>
    /// <param name="generator">Generator that creates the signature.</param>
    /// <returns>A new signed request message owned by the caller.</returns>
    public HttpRequestMessage BuildSigned(WebhookSignatureGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return BuildSignedCore(generator, null, null, _signatureHeaderName);
    }

    /// <summary>Builds a request with a caller-defined signing-input transform.</summary>
    /// <param name="generator">Generator that creates the signature.</param>
    /// <param name="customSigningInput">Transform that returns the exact bytes to sign.</param>
    /// <returns>A new signed request message owned by the caller.</returns>
    public HttpRequestMessage BuildSigned(
        WebhookSignatureGenerator generator,
        Func<ReadOnlyMemory<byte>, string?, ReadOnlyMemory<byte>> customSigningInput)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(customSigningInput);
        return BuildSignedCore(generator, customSigningInput, null, _signatureHeaderName);
    }

    /// <summary>Builds a signed request with an explicit signature header name.</summary>
    /// <param name="generator">Generator that creates the signature.</param>
    /// <param name="signatureHeaderName">Header receiving the signature.</param>
    /// <returns>A new signed request message owned by the caller.</returns>
    public HttpRequestMessage BuildSigned(
        WebhookSignatureGenerator generator,
        string signatureHeaderName)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ValidateRequiredText(signatureHeaderName, nameof(signatureHeaderName));
        return BuildSignedCore(generator, null, null, signatureHeaderName);
    }

    /// <summary>Builds a signed request with a custom input transform and header name.</summary>
    /// <param name="generator">Generator that creates the signature.</param>
    /// <param name="customSigningInput">Transform that returns the exact bytes to sign.</param>
    /// <param name="signatureHeaderName">Header receiving the signature.</param>
    /// <returns>A new signed request message owned by the caller.</returns>
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

    /// <summary>Builds a signed request using explicit signature settings.</summary>
    /// <param name="secret">Secret used only by the test generator.</param>
    /// <param name="algorithm">HMAC algorithm.</param>
    /// <param name="encoding">Signature encoding.</param>
    /// <param name="input">Signing input mode.</param>
    /// <param name="timestampSeparator">Optional timestamp separator.</param>
    /// <param name="signatureHeaderName">Optional signature header override.</param>
    /// <param name="rotationSecrets">Optional verification rotation secrets.</param>
    /// <returns>A new signed request message owned by the caller.</returns>
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

    /// <summary>Compatibility alias for <see cref="BuildSigned(WebhookSignatureGenerator)"/>.</summary>
    /// <param name="generator">Generator that creates the signature.</param>
    /// <returns>A new signed request message owned by the caller.</returns>
    public HttpRequestMessage Sign(WebhookSignatureGenerator generator)
    {
        return BuildSigned(generator);
    }

    /// <summary>Compatibility alias for <see cref="BuildSigned(string, WebhookHashAlgorithm, WebhookSignatureEncoding, WebhookSignatureInput, string?, string?, IEnumerable{string}?)"/>.</summary>
    /// <param name="secret">Secret used only by the test generator.</param>
    /// <param name="algorithm">HMAC algorithm.</param>
    /// <param name="encoding">Signature encoding.</param>
    /// <param name="input">Signing input mode.</param>
    /// <param name="timestampSeparator">Optional timestamp separator.</param>
    /// <param name="signatureHeaderName">Optional signature header override.</param>
    /// <param name="rotationSecrets">Optional verification rotation secrets.</param>
    /// <returns>A new signed request message owned by the caller.</returns>
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

    /// <summary>Returns the builder type name without exposing body or header values.</summary>
    /// <returns>The type name.</returns>
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
