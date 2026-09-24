namespace WebhookKit.Core.Processing;

/// <summary>Outcome of dispatching a verified event to handlers.</summary>
public enum WebhookDispatchStatus
{
    /// <summary>At least one handler completed successfully.</summary>
    Processed = 0,
    /// <summary>No handler was registered for the event type.</summary>
    Ignored = 1,
    /// <summary>Dispatch failed according to the configured retry policy.</summary>
    Failed = 2
}

/// <summary>Classifies why a dispatch failed.</summary>
public enum WebhookDispatchFailureKind
{
    /// <summary>No failure occurred.</summary>
    None = 0,
    /// <summary>The payload or required event metadata was invalid.</summary>
    Payload = 1,
    /// <summary>An application handler failed.</summary>
    Handler = 2,
    /// <summary>The dispatch result could not be resolved.</summary>
    Resolution = 3
}

/// <summary>Safe result of handler dispatch, with optional internal exception details.</summary>
public sealed class WebhookDispatchResult
{
    private WebhookDispatchResult(
        WebhookDispatchStatus status,
        WebhookDispatchFailureKind failureKind,
        string? failureCode,
        Exception? failureException)
    {
        Status = status;
        FailureKind = failureKind;
        FailureCode = failureCode;
        FailureException = failureException;
    }

    /// <summary>Overall dispatch status.</summary>
    public WebhookDispatchStatus Status { get; }

    /// <summary>Classification of a failure, or <see cref="WebhookDispatchFailureKind.None"/>.</summary>
    public WebhookDispatchFailureKind FailureKind { get; }

    /// <summary>Stable safe failure code, when dispatch failed.</summary>
    public string? FailureCode { get; }

    internal Exception? FailureException { get; }

    /// <summary>Returns a status and safe code without exposing exception details.</summary>
    /// <returns>A compact diagnostic string.</returns>
    public override string ToString()
    {
        return FailureCode is null
            ? Status.ToString()
            : $"{Status}:{FailureCode}";
    }

    /// <summary>Creates a successful dispatch result.</summary>
    /// <returns>A processed result.</returns>
    public static WebhookDispatchResult Processed()
    {
        return new WebhookDispatchResult(WebhookDispatchStatus.Processed, WebhookDispatchFailureKind.None, null, null);
    }

    /// <summary>Creates an ignored-event result.</summary>
    /// <returns>An ignored result.</returns>
    public static WebhookDispatchResult Ignored()
    {
        return new WebhookDispatchResult(WebhookDispatchStatus.Ignored, WebhookDispatchFailureKind.None, null, null);
    }

    /// <summary>Creates a failed dispatch result with a safe code.</summary>
    /// <param name="failureKind">The failure classification.</param>
    /// <param name="failureCode">A code containing only safe diagnostic characters.</param>
    /// <returns>A failed result.</returns>
    public static WebhookDispatchResult Failed(WebhookDispatchFailureKind failureKind, string failureCode)
    {
        return Failed(failureKind, failureCode, null);
    }

    internal static WebhookDispatchResult Failed(
        WebhookDispatchFailureKind failureKind,
        string failureCode,
        Exception? failureException)
    {
        ValidateFailureCode(failureCode);
        return new WebhookDispatchResult(WebhookDispatchStatus.Failed, failureKind, failureCode, failureException);
    }

    private static void ValidateFailureCode(string failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode) || failureCode.Length > 128)
        {
            throw new ArgumentException("Failure code must be a safe non-empty code.", nameof(failureCode));
        }

        foreach (var character in failureCode)
        {
            if (character is not (>= 'a' and <= 'z') and
                not (>= 'A' and <= 'Z') and
                not (>= '0' and <= '9') and
                not ('-' or '_' or '.'))
            {
                throw new ArgumentException("Failure code must be a safe non-empty code.", nameof(failureCode));
            }
        }
    }
}
