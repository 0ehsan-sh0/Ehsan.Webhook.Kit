using WebhookKit.Abstractions;
using WebhookKit.Abstractions.Exceptions;
using WebhookKit.Core.Options;
using WebhookKit.Core.Processing;

namespace WebhookKit.Core.Retries;

public interface IWebhookRetryExecutor
{
    Task<WebhookDispatchResult> ExecuteAsync(
        IWebhookDispatchProcessor processor,
        WebhookContext context,
        WebhookRetryOptions options,
        Func<int, CancellationToken, Task>? persistAttemptAsync = null,
        Func<int, TimeSpan, string, CancellationToken, Task>? retryAsync = null,
        int firstAttempt = 1,
        CancellationToken cancellationToken = default);
}

public sealed class WebhookRetryExecutor : IWebhookRetryExecutor
{
    private readonly WebhookRetryPolicy _policy;
    private readonly IWebhookRetryDelay _delay;

    public WebhookRetryExecutor(
        WebhookRetryPolicy policy,
        IWebhookRetryDelay? delay = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _delay = delay ?? new TaskWebhookRetryDelay();
    }

    public async Task<WebhookDispatchResult> ExecuteAsync(
        IWebhookDispatchProcessor processor,
        WebhookContext context,
        WebhookRetryOptions options,
        Func<int, CancellationToken, Task>? persistAttemptAsync = null,
        Func<int, TimeSpan, string, CancellationToken, Task>? retryAsync = null,
        int firstAttempt = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(context);
        WebhookRetryPolicy.ValidateOptions(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstAttempt, 1);

        if (firstAttempt > options.MaxAttempts)
        {
            return WebhookDispatchResult.Failed(
                WebhookDispatchFailureKind.Handler,
                "retry-limit-exhausted");
        }

        for (var attempt = firstAttempt; attempt <= options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WebhookDispatchResult? result;
            try
            {
                result = await processor.DispatchAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WebhookRetryableException exception)
            {
                result = WebhookDispatchResult.Failed(
                    WebhookDispatchFailureKind.Handler,
                    "retryable-failure",
                    exception);
            }
            catch (WebhookPermanentException exception)
            {
                result = WebhookDispatchResult.Failed(
                    WebhookDispatchFailureKind.Handler,
                    "permanent-failure",
                    exception);
            }
            catch (WebhookPayloadException exception)
            {
                result = WebhookDispatchResult.Failed(
                    WebhookDispatchFailureKind.Payload,
                    "payload-invalid",
                    exception);
            }
            catch (Exception exception)
            {
                result = WebhookDispatchResult.Failed(
                    WebhookDispatchFailureKind.Handler,
                    "handler-failed",
                    exception);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (result is null)
            {
                result = WebhookDispatchResult.Failed(
                    WebhookDispatchFailureKind.Handler,
                    "dispatch-result-missing");
            }

            result = NormalizeFailure(result);
            if (persistAttemptAsync is not null)
            {
                await persistAttemptAsync(attempt, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (result.Status != WebhookDispatchStatus.Failed)
            {
                return result;
            }

            if (!WebhookRetryClassifier.ShouldRetry(result) || attempt >= options.MaxAttempts)
            {
                return result;
            }

            var nextDelay = _policy.CalculateDelay(attempt, options, _policy.GetJitterFactor());
            var failureCode = result.FailureCode ?? "retryable-failure";
            if (retryAsync is not null)
            {
                await retryAsync(attempt, nextDelay, failureCode, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _delay.DelayAsync(nextDelay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("The retry executor completed without a dispatch result.");
    }

    private static WebhookDispatchResult NormalizeFailure(WebhookDispatchResult result)
    {
        var classification = WebhookRetryClassifier.Classify(result);
        return classification switch
        {
            WebhookRetryClassification.Retryable => WebhookDispatchResult.Failed(
                WebhookDispatchFailureKind.Handler,
                "retryable-failure",
                result.FailureException),
            WebhookRetryClassification.Permanent => WebhookDispatchResult.Failed(
                WebhookDispatchFailureKind.Handler,
                "permanent-failure",
                result.FailureException),
            WebhookRetryClassification.Payload => WebhookDispatchResult.Failed(
                WebhookDispatchFailureKind.Payload,
                "payload-invalid",
                result.FailureException),
            _ => result
        };
    }
}
