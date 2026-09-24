// Copyright (c) Ehsan. Licensed under the MIT License.
using Microsoft.Extensions.Options;
using WebhookKit.Core.Retries;

namespace WebhookKit.Core.Options;

/// <summary>
/// Startup validation for <see cref="WebhookKitOptions"/>. Failures never include secret values.
/// </summary>
internal sealed class WebhookKitOptionsValidator : IValidateOptions<WebhookKitOptions>
{
    /// <summary>Validates global and per-provider options without exposing secret values.</summary>
    /// <param name="name">Named options instance, when supplied by the options system.</param>
    /// <param name="options">Options to validate; the validator does not mutate them.</param>
    /// <returns>A success, skip, or safe failure result.</returns>
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

        var background = options.Background;
        if (background is null)
        {
            return ValidateOptionsResult.Fail("WebhookKit: Background configuration must be provided.");
        }

        if (background.WorkerConcurrency <= 0)
        {
            return ValidateOptionsResult.Fail("WebhookKit: Background WorkerConcurrency must be greater than zero.");
        }

        if (background.RecoveryInterval <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("WebhookKit: Background RecoveryInterval must be positive.");
        }

        if (background.RecoveryBatchSize <= 0)
        {
            return ValidateOptionsResult.Fail("WebhookKit: Background RecoveryBatchSize must be greater than zero.");
        }

        if (background.LeaseDuration <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("WebhookKit: Background LeaseDuration must be positive.");
        }

        if (background.RecoveryAge < TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("WebhookKit: Background RecoveryAge must be non-negative.");
        }

        if (background.Enabled && !options.Storage.PersistRawBody)
        {
            return ValidateOptionsResult.Fail("WebhookKit: Background processing requires raw body persistence.");
        }

        foreach (var (providerName, provider) in options.Providers)
        {
            var failure = ValidateProvider(providerName, provider, background.LeaseDuration);
            if (failure is not null)
            {
                return failure;
            }
        }

        return ValidateOptionsResult.Success;
    }

    private static ValidateOptionsResult? ValidateProvider(
        string providerName,
        WebhookProviderOptions provider,
        TimeSpan leaseDuration)
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

        if (retry.BackoffMultiplier < 1 || double.IsNaN(retry.BackoffMultiplier) || double.IsInfinity(retry.BackoffMultiplier))
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' retry BackoffMultiplier must be at least 1.");
        }

        if (retry.JitterRatio < 0 || retry.JitterRatio > 1 || double.IsNaN(retry.JitterRatio) || double.IsInfinity(retry.JitterRatio))
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' retry JitterRatio must be between 0 and 1.");
        }

        if (leaseDuration <= WebhookRetryPolicy.CalculateMaximumRetryWindow(retry))
        {
            return ValidateOptionsResult.Fail($"WebhookKit: provider '{providerName}' lease duration must exceed its maximum retry window.");
        }

        return null;
    }
}
