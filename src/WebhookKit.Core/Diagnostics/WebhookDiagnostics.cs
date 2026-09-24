using System.Diagnostics;

namespace WebhookKit.Core.Diagnostics;

/// <summary>Provides the WebhookKit activity source for distributed tracing.</summary>
public static class WebhookDiagnostics
{
    /// <summary>Activity source used for receive and processing spans; the source is safe to share across requests.</summary>
    public static ActivitySource Source { get; } = new("WebhookKit", "1.0.0");

    internal const string WebhookIdTag = "webhook.id";
    internal const string ProviderTag = "webhook.provider";
    internal const string EventIdTag = "webhook.event_id";
    internal const string EventTypeTag = "webhook.event_type";
    internal const string StatusTag = "webhook.status";
    internal const string CorrelationIdTag = "correlation.id";

    internal static Activity? StartReceive(
        string webhookId,
        string provider,
        string? correlationId)
    {
        var activity = Source.StartActivity("WebhookKit.Receive", ActivityKind.Server);
        SetTags(activity, webhookId, provider, null, null, "Received", correlationId);
        return activity;
    }

    internal static Activity? StartProcess(
        string webhookId,
        string provider,
        string? eventId,
        string? eventType,
        string? correlationId)
    {
        var activity = Source.StartActivity("WebhookKit.Process", ActivityKind.Internal);
        SetTags(activity, webhookId, provider, eventId, eventType, "Processing", correlationId);
        return activity;
    }

    internal static void SetTags(
        Activity? activity,
        string webhookId,
        string provider,
        string? eventId,
        string? eventType,
        string status,
        string? correlationId)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(WebhookIdTag, webhookId);
        activity.SetTag(ProviderTag, provider);
        activity.SetTag(EventIdTag, eventId);
        activity.SetTag(EventTypeTag, eventType);
        activity.SetTag(StatusTag, status);
        activity.SetTag(CorrelationIdTag, correlationId);
    }

    internal static void SetResult(Activity? activity, string status, bool error)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(StatusTag, status);
        activity.SetStatus(error ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
    }

    internal static string CreateCorrelationId()
    {
        return Guid.NewGuid().ToString("N");
    }
}
