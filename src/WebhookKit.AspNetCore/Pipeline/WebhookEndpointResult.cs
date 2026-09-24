using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using WebhookKit.Abstractions;

namespace WebhookKit.AspNetCore.Pipeline;

public enum WebhookEndpointOutcome
{
    Processed = 0,
    Accepted = 1,
    Duplicate = 2,
    Ignored = 3,
    InvalidSignature = 4,
    InvalidTimestamp = 5,
    MissingEventId = 6,
    MissingEventType = 7,
    PayloadInvalid = 8,
    PayloadTooLarge = 9,
    QueueUnavailable = 10,
    ProcessingFailed = 11,
    ConfigurationError = 12,
    SignatureVerificationFailed = InvalidSignature,
    ReplayFailed = InvalidTimestamp,
    MissingTimestamp = InvalidTimestamp,
    PayloadFailure = PayloadInvalid,
    QueueFull = QueueUnavailable,
    ProcessingFailure = ProcessingFailed
}

public sealed class WebhookEndpointResult
{
    private WebhookEndpointResult(
        WebhookEndpointOutcome outcome,
        int statusCode,
        string code,
        string message,
        string? traceId,
        WebhookContext? context)
    {
        Outcome = outcome;
        StatusCode = statusCode;
        Code = code;
        Message = message;
        TraceId = traceId;
        Context = context;
    }

    public WebhookEndpointOutcome Outcome { get; }

    public int StatusCode { get; }

    public string Code { get; }

    public string SafeCode => Code;

    public string Message { get; }

    public string SafeMessage => Message;

    public string? TraceId { get; }

    public WebhookContext? Context { get; }

    public bool IsSuccess => Outcome is WebhookEndpointOutcome.Processed or
        WebhookEndpointOutcome.Accepted or
        WebhookEndpointOutcome.Duplicate or
        WebhookEndpointOutcome.Ignored;

    public override string ToString()
    {
        return $"{Outcome}:{Code}";
    }

    internal static WebhookEndpointResult Create(
        HttpContext httpContext,
        WebhookEndpointOutcome outcome,
        int defaultStatusCode,
        string code,
        string message,
        WebhookContext? context,
        WebhookEndpointOptions options)
    {
        var statusCode = options.Response.GetStatusCode(outcome) ?? defaultStatusCode;
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        var traceId = Activity.Current?.TraceId.ToString();
        if (string.IsNullOrWhiteSpace(traceId))
        {
            traceId = httpContext.TraceIdentifier;
        }

        return new WebhookEndpointResult(outcome, statusCode, code, message, traceId, context);
    }
}
