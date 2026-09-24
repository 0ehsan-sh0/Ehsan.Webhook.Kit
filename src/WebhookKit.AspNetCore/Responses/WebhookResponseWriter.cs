using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore.Pipeline;

namespace WebhookKit.AspNetCore.Responses;

/// <summary>Writes safe success statuses and problem JSON for endpoint results.</summary>
public sealed class WebhookResponseWriter
{
    private readonly IWebhookResponseFormatter _formatter;

    /// <summary>Creates a response writer using the supplied formatter.</summary>
    /// <param name="formatter">Formatter that maps failures to safe problem details.</param>
    public WebhookResponseWriter(IWebhookResponseFormatter formatter)
    {
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
    }

    /// <summary>Writes the result to the current HTTP response.</summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="result">The safe endpoint result.</param>
    /// <param name="cancellationToken">Token used to cancel response writes.</param>
    /// <returns>A task that completes after the response body is written.</returns>
    public async Task WriteAsync(
        HttpContext context,
        WebhookEndpointResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        if (result.StatusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(result));
        }

        context.Response.StatusCode = result.StatusCode;
        if (result.IsSuccess)
        {
            return;
        }

        var traceId = ResolveTraceId(context, result.TraceId);
        var problem = _formatter.Format(result.Outcome, traceId)
            ?? throw new InvalidOperationException("The response formatter returned null.");
        var dto = new WebhookProblemResponse
        {
            Code = problem.Code,
            Message = problem.Message,
            TraceId = string.IsNullOrWhiteSpace(problem.TraceId) ? null : problem.TraceId
        };
        var jsonOptions = context.RequestServices?.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(
                dto,
                jsonOptions,
                "application/problem+json",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? ResolveTraceId(HttpContext context, string? resultTraceId)
    {
        var traceId = resultTraceId;
        if (string.IsNullOrWhiteSpace(traceId))
        {
            traceId = Activity.Current?.TraceId.ToString();
        }

        if (string.IsNullOrWhiteSpace(traceId))
        {
            traceId = context.TraceIdentifier;
        }

        return string.IsNullOrWhiteSpace(traceId) ? null : traceId;
    }
}
