// Copyright (c) Ehsan. Licensed under the MIT License.
using Microsoft.Extensions.Options;

namespace WebhookKit.Core.Options;

/// <summary>
/// Startup validation for <see cref="WebhookKitOptions"/>. Failures never include secret values.
/// </summary>
public sealed class WebhookKitOptionsValidator : IValidateOptions<WebhookKitOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, WebhookKitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxRequestBodySizeBytes <= 0)
        {
            return ValidateOptionsResult.Fail("WebhookKit: MaxRequestBodySizeBytes must be greater than zero.");
        }

        if (options.Storage is null)
        {
            return ValidateOptionsResult.Fail("WebhookKit: Storage configuration must be provided.");
        }

        if (options.Queue is null)
        {
            return ValidateOptionsResult.Fail("WebhookKit: Queue configuration must be provided.");
        }

        if (options.Queue.Capacity <= 0)
        {
            return ValidateOptionsResult.Fail("WebhookKit: Queue Capacity must be greater than zero.");
        }

        foreach (var (providerName, provider) in options.Providers)
        {
            var failure = ValidateProvider(providerName, provider);
            if (failure is not null)
            {
                return failure;
            }
        }

        return ValidateOptionsResult.Success;
    }

    private static ValidateOptionsResult? ValidateProvider(string providerName, WebhookProviderOptions provider)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return ValidateOptionsResult.Fail("WebhookKit: provider name must be non-empty.");
        }

        var signature = provider.Signature;
        if (!Enum.IsDefined(signature.Algorithm))
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' has an unknown signature algorithm.");
        }

        if (!Enum.IsDefined(signature.Encoding))
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' has an unknown signature encoding.");
        }

        if (!Enum.IsDefined(signature.Input))
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' has an unknown signature input mode.");
        }

        var signatureConfigured = !string.IsNullOrWhiteSpace(signature.HeaderName)
            || !string.IsNullOrWhiteSpace(signature.Secret)
            || signature.AdditionalSecrets.Count > 0;

        if (signatureConfigured)
        {
            if (string.IsNullOrWhiteSpace(signature.HeaderName))
            {
                return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' configures a secret but no signature header name.");
            }

            var hasSecret = !string.IsNullOrWhiteSpace(signature.Secret)
                || signature.AdditionalSecrets.Any(s => !string.IsNullOrWhiteSpace(s));

            if (!hasSecret)
            {
                return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' configures signature verification but no secret.");
            }
        }

        var timestamp = provider.Timestamp;
        if (timestamp.Tolerance <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' timestamp tolerance must be positive.");
        }

        if (string.IsNullOrWhiteSpace(timestamp.HeaderName) && !timestamp.AllowMissing)
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' must configure a timestamp header or explicitly allow missing timestamps.");
        }

        if (signature.Input == WebhookSignatureInput.TimestampPrefixedRawBody &&
            string.IsNullOrWhiteSpace(timestamp.HeaderName))
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' timestamp-prefixed signatures require a timestamp header.");
        }

        if (signature.Input == WebhookSignatureInput.TimestampPrefixedRawBody && signature.TimestampSeparator is null)
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' timestamp-prefixed signatures require a separator.");
        }

        if (provider.MaxRequestBodySizeBytes is <= 0)
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' MaxRequestBodySizeBytes must be greater than zero when configured.");
        }

        if (provider.EventIdHeaderName is not null && string.IsNullOrWhiteSpace(provider.EventIdHeaderName))
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' has an empty event ID header name.");
        }

        if (provider.EventTypeHeaderName is not null && string.IsNullOrWhiteSpace(provider.EventTypeHeaderName))
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' has an empty event type header name.");
        }

        var retry = provider.Retry;
        if (retry.MaxAttempts < 1)
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' retry MaxAttempts must be at least 1.");
        }

        if (retry.InitialDelay < TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' retry InitialDelay must be non-negative.");
        }

        if (retry.BackoffMultiplier < 1)
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' retry BackoffMultiplier must be at least 1.");
        }

        return null;
    }
}
