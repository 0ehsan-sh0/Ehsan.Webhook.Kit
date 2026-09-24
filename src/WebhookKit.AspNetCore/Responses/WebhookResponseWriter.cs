using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore.Pipeline;

namespace WebhookKit.AspNetCore.Responses;

public sealed class WebhookResponseWriter
{
    private readonly IWebhookResponseFormatter _formatter;

    public WebhookResponseWriter(IWebhookResponseFormatter formatter)
    {
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
    }

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
