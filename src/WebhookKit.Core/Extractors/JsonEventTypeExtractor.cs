// Copyright (c) Ehsan. Licensed under the MIT License.
using WebhookKit.Abstractions;

namespace WebhookKit.Core.Extractors;

/// <summary>
/// Extracts event type from JSON payload body using a configured property path (default: "type").
/// </summary>
public sealed class JsonEventTypeExtractor : IWebhookEventTypeExtractor
{
    private readonly string _propertyPath;

    public JsonEventTypeExtractor(string propertyPath = "type")
    {
        if (string.IsNullOrWhiteSpace(propertyPath))
        {
            throw new ArgumentException("Property path must not be empty.", nameof(propertyPath));
        }

        _propertyPath = propertyPath;
    }

    public ValueTask<string?> ExtractAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.RawBody == null || context.RawBody.Length == 0)
        {
            return ValueTask.FromResult<string?>(null);
        }

        string? result = JsonPathHelper.ExtractValueByPath(context.RawBody, _propertyPath);
        return ValueTask.FromResult(result);
    }
}
