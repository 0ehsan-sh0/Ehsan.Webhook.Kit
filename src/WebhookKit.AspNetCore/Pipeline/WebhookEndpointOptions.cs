namespace WebhookKit.AspNetCore.Pipeline;

public enum WebhookProcessingMode
{
    Synchronous = 0,
    Asynchronous = 1,
    Async = Asynchronous,
    Background = Asynchronous
}

public sealed class WebhookEndpointResponseOptions
{
    private int? _successStatusCode;
    private int? _acceptedStatusCode;
    private int? _duplicateStatusCode;
    private int? _ignoredStatusCode;
    private int? _invalidSignatureStatusCode;
    private int? _invalidTimestampStatusCode;
    private int? _missingEventIdStatusCode;
    private int? _missingEventTypeStatusCode;
    private int? _payloadInvalidStatusCode;
    private int? _payloadTooLargeStatusCode;
    private int? _queueUnavailableStatusCode;
    private int? _processingFailedStatusCode;
    private int? _configurationErrorStatusCode;

    public int? SuccessStatusCode
    {
        get => _successStatusCode;
        init => _successStatusCode = value;
    }

    public int? ProcessedStatusCode
    {
        get => _successStatusCode;
        init => _successStatusCode = value;
    }

    public int? AcceptedStatusCode
    {
        get => _acceptedStatusCode;
        init => _acceptedStatusCode = value;
    }

    public int? DuplicateStatusCode
    {
        get => _duplicateStatusCode;
        init => _duplicateStatusCode = value;
    }

    public int? IgnoredStatusCode
    {
        get => _ignoredStatusCode;
        init => _ignoredStatusCode = value;
    }

    public int? InvalidSignatureStatusCode
    {
        get => _invalidSignatureStatusCode;
        init => _invalidSignatureStatusCode = value;
    }

    public int? SignatureFailureStatusCode
    {
        get => _invalidSignatureStatusCode;
        init => _invalidSignatureStatusCode = value;
    }

    public int? InvalidTimestampStatusCode
    {
        get => _invalidTimestampStatusCode;
        init => _invalidTimestampStatusCode = value;
    }

    public int? ReplayFailureStatusCode
    {
        get => _invalidTimestampStatusCode;
        init => _invalidTimestampStatusCode = value;
    }

    public int? MissingEventIdStatusCode
    {
        get => _missingEventIdStatusCode;
        init => _missingEventIdStatusCode = value;
    }

    public int? MissingEventTypeStatusCode
    {
        get => _missingEventTypeStatusCode;
        init => _missingEventTypeStatusCode = value;
    }

    public int? PayloadInvalidStatusCode
    {
        get => _payloadInvalidStatusCode;
        init => _payloadInvalidStatusCode = value;
    }

    public int? PayloadFailureStatusCode
    {
        get => _payloadInvalidStatusCode;
        init => _payloadInvalidStatusCode = value;
    }

    public int? PayloadTooLargeStatusCode
    {
        get => _payloadTooLargeStatusCode;
        init => _payloadTooLargeStatusCode = value;
    }

    public int? QueueUnavailableStatusCode
    {
        get => _queueUnavailableStatusCode;
        init => _queueUnavailableStatusCode = value;
    }

    public int? QueueFullStatusCode
    {
        get => _queueUnavailableStatusCode;
        init => _queueUnavailableStatusCode = value;
    }

    public int? ProcessingFailedStatusCode
    {
        get => _processingFailedStatusCode;
        init => _processingFailedStatusCode = value;
    }

    public int? ProcessingFailureStatusCode
    {
        get => _processingFailedStatusCode;
        init => _processingFailedStatusCode = value;
    }

    public int? ConfigurationErrorStatusCode
    {
        get => _configurationErrorStatusCode;
        init => _configurationErrorStatusCode = value;
    }

    public Func<WebhookEndpointOutcome, int>? StatusCodeSelector { get; init; }

    internal int? GetStatusCode(WebhookEndpointOutcome outcome)
    {
        return StatusCodeSelector?.Invoke(outcome) ?? outcome switch
        {
            WebhookEndpointOutcome.Processed => SuccessStatusCode,
            WebhookEndpointOutcome.Accepted => AcceptedStatusCode,
            WebhookEndpointOutcome.Duplicate => DuplicateStatusCode,
            WebhookEndpointOutcome.Ignored => IgnoredStatusCode,
            WebhookEndpointOutcome.InvalidSignature => InvalidSignatureStatusCode,
            WebhookEndpointOutcome.InvalidTimestamp => InvalidTimestampStatusCode,
            WebhookEndpointOutcome.MissingEventId => MissingEventIdStatusCode,
            WebhookEndpointOutcome.MissingEventType => MissingEventTypeStatusCode,
            WebhookEndpointOutcome.PayloadInvalid => PayloadInvalidStatusCode,
            WebhookEndpointOutcome.PayloadTooLarge => PayloadTooLargeStatusCode,
            WebhookEndpointOutcome.QueueUnavailable => QueueUnavailableStatusCode,
            WebhookEndpointOutcome.ProcessingFailed => ProcessingFailedStatusCode,
            WebhookEndpointOutcome.ConfigurationError => ConfigurationErrorStatusCode,
            _ => null
        };
    }

    internal void Validate()
    {
        ValidateStatusCode(StatusCodeSelector is null ? null : StatusCodeSelector(WebhookEndpointOutcome.Processed), nameof(StatusCodeSelector));
        foreach (var outcome in Enum.GetValues<WebhookEndpointOutcome>())
        {
            ValidateStatusCode(GetStatusCode(outcome), nameof(WebhookEndpointResponseOptions));
        }
    }

    private static void ValidateStatusCode(int? statusCode, string parameterName)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Webhook status codes must be between 100 and 599.");
        }
    }
}

public sealed class WebhookEndpointOptions
{
    private string[]? _tags;

    public WebhookEndpointOptions()
    {
    }

    public WebhookEndpointOptions(string providerName)
    {
        ProviderName = providerName;
    }

    public string ProviderName { get; init; } = string.Empty;

    public string Provider
    {
        get => ProviderName;
        init => ProviderName = value;
    }

    public WebhookProcessingMode Mode { get; init; } = WebhookProcessingMode.Synchronous;

    public WebhookProcessingMode ProcessingMode
    {
        get => Mode;
        init => Mode = value;
    }

    public bool IncludeInSchema { get; init; } = true;

    public string? OperationId { get; init; }

    public string? EndpointName
    {
        get => OperationId;
        init => OperationId = value;
    }

    public string? Summary { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Tags
    {
        get => _tags ?? Array.Empty<string>();
        init => _tags = value?.ToArray() ?? [];
    }

    public WebhookEndpointResponseOptions Response { get; init; } = new();

    public WebhookEndpointResponseOptions ResponseOptions
    {
        get => Response;
        init => Response = value;
    }

    public WebhookEndpointResponseOptions StatusCodes
    {
        get => Response;
        init => Response = value;
    }

    public WebhookEndpointResponseOptions ResponseStatusCodes
    {
        get => Response;
        init => Response = value;
    }

    internal WebhookEndpointOptions Snapshot()
    {
        return new WebhookEndpointOptions
        {
            ProviderName = ProviderName,
            Mode = Mode,
            IncludeInSchema = IncludeInSchema,
            OperationId = OperationId,
            Summary = Summary,
            Description = Description,
            Tags = _tags?.ToArray() ?? [],
            Response = new WebhookEndpointResponseOptions
            {
                SuccessStatusCode = Response.SuccessStatusCode,
                AcceptedStatusCode = Response.AcceptedStatusCode,
                DuplicateStatusCode = Response.DuplicateStatusCode,
                IgnoredStatusCode = Response.IgnoredStatusCode,
                InvalidSignatureStatusCode = Response.InvalidSignatureStatusCode,
                InvalidTimestampStatusCode = Response.InvalidTimestampStatusCode,
                MissingEventIdStatusCode = Response.MissingEventIdStatusCode,
                MissingEventTypeStatusCode = Response.MissingEventTypeStatusCode,
                PayloadInvalidStatusCode = Response.PayloadInvalidStatusCode,
                PayloadTooLargeStatusCode = Response.PayloadTooLargeStatusCode,
                QueueUnavailableStatusCode = Response.QueueUnavailableStatusCode,
                ProcessingFailedStatusCode = Response.ProcessingFailedStatusCode,
                ConfigurationErrorStatusCode = Response.ConfigurationErrorStatusCode,
                StatusCodeSelector = Response.StatusCodeSelector
            }
        };
    }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderName);
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        (Response ?? throw new ArgumentNullException(nameof(Response))).Validate();
        foreach (var tag in Tags)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        }
    }
}

public sealed class WebhookEndpointMetadata
{
    internal WebhookEndpointMetadata(WebhookEndpointOptions options)
    {
        Options = options.Snapshot();
        ProviderName = options.ProviderName;
    }

    public string ProviderName { get; }

    public WebhookEndpointOptions Options { get; }
}
