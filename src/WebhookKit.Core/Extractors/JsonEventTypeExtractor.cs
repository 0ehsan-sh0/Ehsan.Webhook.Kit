// Copyright (c) Ehsan. Licensed under the MIT License.
using WebhookKit.Abstractions;

namespace WebhookKit.Core.Extractors;

/// <summary>
/// Extracts event type from JSON payload body using a configured property path (default: "type").
/// </summary>
public sealed class JsonEventTypeExtractor : IWebhookEventTypeExtractor
{
    private readonly string _propertyPath;

    /// <summary>Creates a JSON property extractor.</summary>
    /// <param name="propertyPath">Dot-separated object property path, defaulting to <c>type</c>.</param>
    public JsonEventTypeExtractor(string propertyPath = "type")
    {
        if (string.IsNullOrWhiteSpace(propertyPath))
        {
            throw new ArgumentException("Property path must not be empty.", nameof(propertyPath));
        }

        _propertyPath = propertyPath;
    }

    /// <summary>Extracts a scalar value from the configured JSON property path.</summary>
    /// <param name="context">Request metadata and exact body.</param>
    /// <param name="cancellationToken">Token reserved for extractor cancellation.</param>
    /// <returns>The extracted value, or <see langword="null"/> for an absent path or invalid JSON.</returns>
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
