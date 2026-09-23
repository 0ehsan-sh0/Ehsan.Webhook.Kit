namespace WebhookKit.Core.Processing;

public enum WebhookDispatchStatus
{
    Processed = 0,
    Ignored = 1,
    Failed = 2
}

public enum WebhookDispatchFailureKind
{
    None = 0,
    Payload = 1,
    Handler = 2,
    Resolution = 3
}

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

    public WebhookDispatchStatus Status { get; }

    public WebhookDispatchFailureKind FailureKind { get; }

    public string? FailureCode { get; }

    internal Exception? FailureException { get; }

    public override string ToString()
    {
        return FailureCode is null
            ? Status.ToString()
            : $"{Status}:{FailureCode}";
    }

    public static WebhookDispatchResult Processed()
    {
        return new WebhookDispatchResult(WebhookDispatchStatus.Processed, WebhookDispatchFailureKind.None, null, null);
    }

    public static WebhookDispatchResult Ignored()
    {
        return new WebhookDispatchResult(WebhookDispatchStatus.Ignored, WebhookDispatchFailureKind.None, null, null);
    }

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
