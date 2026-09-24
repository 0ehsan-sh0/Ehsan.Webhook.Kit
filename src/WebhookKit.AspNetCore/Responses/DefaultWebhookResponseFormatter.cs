using WebhookKit.Abstractions;

namespace WebhookKit.AspNetCore.Responses;

/// <summary>Default formatter that maps outcomes to safe problem details.</summary>
public sealed class DefaultWebhookResponseFormatter : IWebhookResponseFormatter
{
    /// <summary>Maps an endpoint outcome to a stable code and safe message.</summary>
    /// <param name="outcome">The endpoint outcome.</param>
    /// <param name="traceId">Optional trace identifier.</param>
    /// <returns>Safe problem details without request secrets or raw data.</returns>
    public WebhookResponseProblem Format(WebhookEndpointOutcome outcome, string? traceId = null)
    {
        var (code, message) = outcome switch
        {
            WebhookEndpointOutcome.Processed => ("processed", "Webhook processed."),
            WebhookEndpointOutcome.Accepted => ("accepted", "Webhook accepted."),
            WebhookEndpointOutcome.Duplicate => ("duplicate", "Webhook already received."),
            WebhookEndpointOutcome.Ignored => ("ignored", "Webhook ignored."),
            WebhookEndpointOutcome.InvalidSignature => ("signature-verification-failed", "Webhook signature verification failed."),
            WebhookEndpointOutcome.InvalidTimestamp => ("timestamp-verification-failed", "Webhook timestamp verification failed."),
            WebhookEndpointOutcome.MissingEventId => ("event-id-required", "Webhook event identifier is required."),
            WebhookEndpointOutcome.MissingEventType => ("missing-event-type", "Webhook event type is required."),
            WebhookEndpointOutcome.PayloadInvalid => ("payload-invalid", "Webhook payload is invalid."),
            WebhookEndpointOutcome.PayloadTooLarge => ("payload-too-large", "Webhook payload is too large."),
            WebhookEndpointOutcome.QueueUnavailable => ("queue-unavailable", "Webhook processing is temporarily unavailable."),
            WebhookEndpointOutcome.ProcessingFailed => ("handler-failed", "Webhook processing failed."),
            WebhookEndpointOutcome.ConfigurationError => ("webhook-configuration-error", "Webhook processing is not configured."),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

        return new WebhookResponseProblem(code, message, traceId);
    }
}
