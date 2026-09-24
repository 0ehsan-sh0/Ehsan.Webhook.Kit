using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using WebhookKit.Abstractions;

namespace WebhookKit.AspNetCore.Pipeline;

/// <summary>Safe result returned by the ASP.NET Core endpoint pipeline.</summary>
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

    /// <summary>Endpoint outcome selected by the pipeline.</summary>
    public WebhookEndpointOutcome Outcome { get; }

    /// <summary>HTTP status selected for the outcome.</summary>
    public int StatusCode { get; }

    /// <summary>Stable machine-readable response code.</summary>
    public string Code { get; }

    /// <summary>Compatibility alias for <see cref="Code"/>.</summary>
    public string SafeCode => Code;

    /// <summary>Safe human-readable response message.</summary>
    public string Message { get; }

    /// <summary>Compatibility alias for <see cref="Message"/>.</summary>
    public string SafeMessage => Message;

    /// <summary>Optional trace identifier for support correlation.</summary>
    public string? TraceId { get; }

    /// <summary>Verified context when one was created; raw payload details remain private to the context.</summary>
    public WebhookContext? Context { get; }

    /// <summary>Whether the outcome is an acknowledgement success.</summary>
    public bool IsSuccess => Outcome is WebhookEndpointOutcome.Processed or
        WebhookEndpointOutcome.Accepted or
        WebhookEndpointOutcome.Duplicate or
        WebhookEndpointOutcome.Ignored;

    /// <summary>Returns the outcome and safe code without response secrets.</summary>
    /// <returns>A compact diagnostic string.</returns>
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

        return new WebhookEndpointResult(
            outcome,
            statusCode,
            code,
            message,
            string.IsNullOrWhiteSpace(traceId) ? null : traceId,
            context);
    }
}
