using WebhookKit.Abstractions.Exceptions;
using WebhookKit.Core.Processing;

namespace WebhookKit.Core.Retries;

public enum WebhookRetryClassification
{
    None = 0,
    Retryable = 1,
    Permanent = 2,
    Payload = 3,
    Cancellation = 4,
    Unknown = 5,
    MissingException = 6,
    MissingResult = 7,
    InvalidResult = 8
}

public static class WebhookRetryClassifier
{
    public static WebhookRetryClassification Classify(Exception? exception)
    {
        return exception switch
        {
            WebhookRetryableException => WebhookRetryClassification.Retryable,
            WebhookPermanentException => WebhookRetryClassification.Permanent,
            WebhookPayloadException => WebhookRetryClassification.Payload,
            OperationCanceledException => WebhookRetryClassification.Cancellation,
            null => WebhookRetryClassification.MissingException,
            _ => WebhookRetryClassification.Unknown
        };
    }

    public static WebhookRetryClassification Classify(WebhookDispatchResult? result)
    {
        if (result is null)
        {
            return WebhookRetryClassification.MissingResult;
        }

        if (!Enum.IsDefined(result.Status))
        {
            return WebhookRetryClassification.InvalidResult;
        }

        if (result.Status != WebhookDispatchStatus.Failed)
        {
            return WebhookRetryClassification.None;
        }

        return Classify(result.FailureException);
    }

    public static bool IsRetryable(Exception? exception)
    {
        return Classify(exception) == WebhookRetryClassification.Retryable;
    }

    public static bool ShouldRetry(WebhookDispatchResult? result)
    {
        return Classify(result) == WebhookRetryClassification.Retryable;
    }
}
