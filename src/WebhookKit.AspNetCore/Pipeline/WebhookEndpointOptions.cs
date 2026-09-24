using System.Collections.ObjectModel;
using WebhookKit.Abstractions;

namespace WebhookKit.AspNetCore.Pipeline;

/// <summary>Controls whether an endpoint handles a delivery synchronously or admits it for background work.</summary>
public enum WebhookProcessingMode
{
    /// <summary>Verify, deduplicate, and dispatch before responding.</summary>
    Synchronous = 0,
    /// <summary>Verify, deduplicate, persist, and enqueue before responding with acceptance.</summary>
    Asynchronous = 1,
    /// <summary>Compatibility alias for <see cref="Asynchronous"/>.</summary>
    Async = Asynchronous,
    /// <summary>Compatibility alias for <see cref="Asynchronous"/>.</summary>
    Background = Asynchronous
}

/// <summary>Configures safe HTTP status codes for endpoint outcomes.</summary>
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

    /// <summary>HTTP status for a synchronously processed delivery.</summary>
    public int? SuccessStatusCode
    {
        get => _successStatusCode;
        init => _successStatusCode = value;
    }

    /// <summary>Compatibility alias for <see cref="SuccessStatusCode"/>.</summary>
    public int? ProcessedStatusCode
    {
        get => _successStatusCode;
        init => _successStatusCode = value;
    }

    /// <summary>HTTP status for an asynchronously accepted delivery.</summary>
    public int? AcceptedStatusCode
    {
        get => _acceptedStatusCode;
        init => _acceptedStatusCode = value;
    }

    /// <summary>HTTP status for a duplicate delivery.</summary>
    public int? DuplicateStatusCode
    {
        get => _duplicateStatusCode;
        init => _duplicateStatusCode = value;
    }

    /// <summary>HTTP status for a verified event with no handler.</summary>
    public int? IgnoredStatusCode
    {
        get => _ignoredStatusCode;
        init => _ignoredStatusCode = value;
    }

    /// <summary>HTTP status for a signature rejection.</summary>
    public int? InvalidSignatureStatusCode
    {
        get => _invalidSignatureStatusCode;
        init => _invalidSignatureStatusCode = value;
    }

    /// <summary>Compatibility alias for <see cref="InvalidSignatureStatusCode"/>.</summary>
    public int? SignatureFailureStatusCode
    {
        get => _invalidSignatureStatusCode;
        init => _invalidSignatureStatusCode = value;
    }

    /// <summary>HTTP status for a timestamp or replay rejection.</summary>
    public int? InvalidTimestampStatusCode
    {
        get => _invalidTimestampStatusCode;
        init => _invalidTimestampStatusCode = value;
    }

    /// <summary>Compatibility alias for <see cref="InvalidTimestampStatusCode"/>.</summary>
    public int? ReplayFailureStatusCode
    {
        get => _invalidTimestampStatusCode;
        init => _invalidTimestampStatusCode = value;
    }

    /// <summary>HTTP status for a missing event identifier.</summary>
    public int? MissingEventIdStatusCode
    {
        get => _missingEventIdStatusCode;
        init => _missingEventIdStatusCode = value;
    }

    /// <summary>HTTP status for a missing event type.</summary>
    public int? MissingEventTypeStatusCode
    {
        get => _missingEventTypeStatusCode;
        init => _missingEventTypeStatusCode = value;
    }

    /// <summary>HTTP status for an invalid payload.</summary>
    public int? PayloadInvalidStatusCode
    {
        get => _payloadInvalidStatusCode;
        init => _payloadInvalidStatusCode = value;
    }

    /// <summary>Compatibility alias for <see cref="PayloadInvalidStatusCode"/>.</summary>
    public int? PayloadFailureStatusCode
    {
        get => _payloadInvalidStatusCode;
        init => _payloadInvalidStatusCode = value;
    }

    /// <summary>HTTP status for a body that exceeds the effective limit.</summary>
    public int? PayloadTooLargeStatusCode
    {
        get => _payloadTooLargeStatusCode;
        init => _payloadTooLargeStatusCode = value;
    }

    /// <summary>HTTP status when the asynchronous queue is unavailable.</summary>
    public int? QueueUnavailableStatusCode
    {
        get => _queueUnavailableStatusCode;
        init => _queueUnavailableStatusCode = value;
    }

    /// <summary>Compatibility alias for <see cref="QueueUnavailableStatusCode"/>.</summary>
    public int? QueueFullStatusCode
    {
        get => _queueUnavailableStatusCode;
        init => _queueUnavailableStatusCode = value;
    }

    /// <summary>HTTP status for a processing failure.</summary>
    public int? ProcessingFailedStatusCode
    {
        get => _processingFailedStatusCode;
        init => _processingFailedStatusCode = value;
    }

    /// <summary>Compatibility alias for <see cref="ProcessingFailedStatusCode"/>.</summary>
    public int? ProcessingFailureStatusCode
    {
        get => _processingFailedStatusCode;
        init => _processingFailedStatusCode = value;
    }

    /// <summary>HTTP status for a configuration error.</summary>
    public int? ConfigurationErrorStatusCode
    {
        get => _configurationErrorStatusCode;
        init => _configurationErrorStatusCode = value;
    }

    /// <summary>Optional callback that selects a status code for any outcome.</summary>
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

/// <summary>Configures one ASP.NET Core webhook endpoint.</summary>
/// <remarks>Instances are snapshotted by endpoint registration so later caller mutation does not change routing behavior.</remarks>
public sealed class WebhookEndpointOptions
{
    private IReadOnlyList<string> _tags = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>Creates empty endpoint options for object initializers.</summary>
    public WebhookEndpointOptions()
    {
    }

    /// <summary>Creates endpoint options for a provider.</summary>
    /// <param name="providerName">Configured provider name.</param>
    public WebhookEndpointOptions(string providerName)
    {
        ProviderName = providerName;
    }

    /// <summary>Configured provider name used to select provider options.</summary>
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>Compatibility alias for <see cref="ProviderName"/>.</summary>
    public string Provider
    {
        get => ProviderName;
        init => ProviderName = value;
    }

    /// <summary>Synchronous or asynchronous processing mode.</summary>
    public WebhookProcessingMode Mode { get; init; } = WebhookProcessingMode.Synchronous;

    /// <summary>Compatibility alias for <see cref="Mode"/>.</summary>
    public WebhookProcessingMode ProcessingMode
    {
        get => Mode;
        init => Mode = value;
    }

    /// <summary>Whether the endpoint is included in generated OpenAPI metadata.</summary>
    public bool IncludeInSchema { get; init; } = true;

    /// <summary>Optional OpenAPI operation identifier and route name.</summary>
    public string? OperationId { get; init; }

    /// <summary>Compatibility alias for <see cref="OperationId"/>.</summary>
    public string? EndpointName
    {
        get => OperationId;
        init => OperationId = value;
    }

    /// <summary>Optional OpenAPI summary.</summary>
    public string? Summary { get; init; }

    /// <summary>Optional OpenAPI description.</summary>
    public string? Description { get; init; }

    /// <summary>Immutable OpenAPI tags copied into the endpoint metadata.</summary>
    public IReadOnlyList<string> Tags
    {
        get => _tags;
        init => _tags = CreateTagSnapshot(value);
    }

    /// <summary>HTTP response status configuration.</summary>
    public WebhookEndpointResponseOptions Response { get; init; } = new();

    /// <summary>Compatibility alias for <see cref="Response"/>.</summary>
    public WebhookEndpointResponseOptions ResponseOptions
    {
        get => Response;
        init => Response = value;
    }

    /// <summary>Compatibility alias for <see cref="Response"/>.</summary>
    public WebhookEndpointResponseOptions StatusCodes
    {
        get => Response;
        init => Response = value;
    }

    /// <summary>Compatibility alias for <see cref="Response"/>.</summary>
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
            Tags = CreateTagSnapshot(_tags),
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

    private static ReadOnlyCollection<string> CreateTagSnapshot(IReadOnlyList<string>? tags)
    {
        return Array.AsReadOnly(tags?.ToArray() ?? []);
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

/// <summary>Immutable endpoint metadata attached to a mapped route.</summary>
public sealed class WebhookEndpointMetadata
{
    internal WebhookEndpointMetadata(WebhookEndpointOptions options)
    {
        Options = options.Snapshot();
        ProviderName = options.ProviderName;
    }

    /// <summary>Provider name associated with the endpoint.</summary>
    public string ProviderName { get; }

    /// <summary>Immutable options snapshot used by endpoint processing.</summary>
    public WebhookEndpointOptions Options { get; }
}
