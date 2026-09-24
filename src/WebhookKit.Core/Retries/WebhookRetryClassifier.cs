using WebhookKit.Abstractions.Exceptions;
using WebhookKit.Core.Processing;

namespace WebhookKit.Core.Retries;

/// <summary>Classifies failures for retry decisions.</summary>
public enum WebhookRetryClassification
{
    /// <summary>The result is successful or has no retryable failure.</summary>
    None = 0,
    /// <summary>The exception explicitly requests a retry.</summary>
    Retryable = 1,
    /// <summary>The exception explicitly rejects retries.</summary>
    Permanent = 2,
    /// <summary>The payload or required metadata is invalid.</summary>
    Payload = 3,
    /// <summary>Cancellation must be propagated.</summary>
    Cancellation = 4,
    /// <summary>The exception type is not recognized.</summary>
    Unknown = 5,
    /// <summary>No exception was supplied.</summary>
    MissingException = 6,
    /// <summary>No dispatch result was supplied.</summary>
    MissingResult = 7,
    /// <summary>The dispatch result contains an invalid status.</summary>
    InvalidResult = 8
}

/// <summary>Maps exceptions and dispatch results to retry classifications.</summary>
public static class WebhookRetryClassifier
{
    /// <summary>Classifies an exception without inspecting its message.</summary>
    /// <param name="exception">The exception to classify; <see langword="null"/> is classified as missing.</param>
    /// <returns>The retry classification.</returns>
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

    /// <summary>Classifies a dispatch result.</summary>
    /// <param name="result">The result to classify; <see langword="null"/> is classified as missing.</param>
    /// <returns>The retry classification.</returns>
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

    /// <summary>Determines whether an exception is explicitly retryable.</summary>
    /// <param name="exception">The exception to inspect.</param>
    /// <returns><see langword="true"/> only for an explicitly retryable failure.</returns>
    public static bool IsRetryable(Exception? exception)
    {
        return Classify(exception) == WebhookRetryClassification.Retryable;
    }

    /// <summary>Determines whether a dispatch result should be retried.</summary>
    /// <param name="result">The dispatch result to inspect.</param>
    /// <returns><see langword="true"/> only for an explicitly retryable failure.</returns>
    public static bool ShouldRetry(WebhookDispatchResult? result)
    {
        return Classify(result) == WebhookRetryClassification.Retryable;
    }
}
