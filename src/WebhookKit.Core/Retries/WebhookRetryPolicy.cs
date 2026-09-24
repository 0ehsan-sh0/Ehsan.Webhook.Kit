using WebhookKit.Core.Options;

namespace WebhookKit.Core.Retries;

/// <summary>Abstraction for waiting between retry attempts.</summary>
public interface IWebhookRetryDelay
{
    /// <summary>Waits for the requested delay.</summary>
    /// <param name="delay">Time to wait; non-positive values complete immediately.</param>
    /// <param name="cancellationToken">Token used to cancel the wait.</param>
    /// <returns>A task representing the wait.</returns>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

/// <summary>Uses <see cref="Task.Delay(TimeSpan, CancellationToken)"/> for retry waits.</summary>
public sealed class TaskWebhookRetryDelay : IWebhookRetryDelay
{
    /// <summary>Waits for the requested delay.</summary>
    /// <param name="delay">Time to wait; non-positive values complete immediately.</param>
    /// <param name="cancellationToken">Token used to cancel the wait.</param>
    /// <returns>A task representing the wait.</returns>
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return delay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(delay, cancellationToken);
    }
}

/// <summary>Calculates exponential retry delays with optional jitter.</summary>
public sealed class WebhookRetryPolicy
{
    private readonly Func<double> _jitterFactorProvider;

    /// <summary>Creates a retry policy.</summary>
    /// <param name="jitterFactorProvider">Optional source of a value from zero through one; defaults to a shared random source.</param>
    public WebhookRetryPolicy(Func<double>? jitterFactorProvider = null)
    {
        _jitterFactorProvider = jitterFactorProvider ?? Random.Shared.NextDouble;
    }

    /// <summary>Gets a validated jitter factor from zero through one.</summary>
    /// <returns>The jitter factor.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The supplied factor is not finite or is outside its range.</exception>
    public double GetJitterFactor()
    {
        var factor = _jitterFactorProvider();
        if (double.IsNaN(factor) || double.IsInfinity(factor) || factor < 0 || factor > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        return factor;
    }

    /// <summary>Calculates the delay before the next retry.</summary>
    /// <param name="failedAttempt">One-based number of the attempt that just failed.</param>
    /// <param name="options">Validated retry settings.</param>
    /// <param name="jitterFactor">Optional deterministic jitter factor for tests.</param>
    /// <returns>The calculated delay, never negative.</returns>
    public TimeSpan CalculateDelay(int failedAttempt, WebhookRetryOptions options, double? jitterFactor = null)
    {
        ValidateOptions(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempt, 1);

        var factor = 0d;
        if (options.UseJitter)
        {
            factor = jitterFactor ?? GetJitterFactor();
            if (double.IsNaN(factor) || double.IsInfinity(factor) || factor < 0 || factor > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(jitterFactor));
            }
        }

        var baseTicks = options.InitialDelay.Ticks * Math.Pow(options.BackoffMultiplier, failedAttempt - 1);
        if (!options.UseJitter || options.JitterRatio == 0)
        {
            return ToTimeSpan(baseTicks);
        }

        var totalTicks = baseTicks + (baseTicks * options.JitterRatio * factor);
        return ToTimeSpan(totalTicks);
    }

    /// <summary>Calculates the maximum non-negative retry window for settings.</summary>
    /// <param name="options">Retry settings to inspect.</param>
    /// <returns>The maximum window, or <see cref="TimeSpan.Zero"/> when no retry is possible.</returns>
    public static TimeSpan GetMaximumRetryWindow(WebhookRetryOptions options)
    {
        ValidateOptions(options);
        if (options.MaxAttempts <= 1 || options.InitialDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var retryCount = options.MaxAttempts - 1;
        var multiplier = options.BackoffMultiplier;
        double totalMilliseconds;
        if (multiplier == 1)
        {
            totalMilliseconds = options.InitialDelay.TotalMilliseconds * retryCount;
        }
        else
        {
            totalMilliseconds = options.InitialDelay.TotalMilliseconds *
                ((Math.Pow(multiplier, retryCount) - 1) / (multiplier - 1));
        }

        if (options.UseJitter)
        {
            totalMilliseconds *= 1 + options.JitterRatio;
        }

        if (double.IsNaN(totalMilliseconds) || double.IsInfinity(totalMilliseconds))
        {
            return TimeSpan.MaxValue;
        }

        return ToTimeSpan(totalMilliseconds * TimeSpan.TicksPerMillisecond);
    }

    /// <summary>Compatibility alias for <see cref="GetMaximumRetryWindow"/>.</summary>
    /// <param name="options">Retry settings to inspect.</param>
    /// <returns>The maximum retry window.</returns>
    public static TimeSpan CalculateMaximumRetryWindow(WebhookRetryOptions options)
    {
        return GetMaximumRetryWindow(options);
    }

    /// <summary>Validates retry settings before calculation.</summary>
    /// <param name="options">Settings to validate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A setting is outside its supported range.</exception>
    public static void ValidateOptions(WebhookRetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.InitialDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.BackoffMultiplier < 1 || double.IsNaN(options.BackoffMultiplier) || double.IsInfinity(options.BackoffMultiplier))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.JitterRatio < 0 || options.JitterRatio > 1 || double.IsNaN(options.JitterRatio) || double.IsInfinity(options.JitterRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static TimeSpan ToTimeSpan(double ticks)
    {
        if (ticks <= 0)
        {
            return TimeSpan.Zero;
        }

        if (ticks >= TimeSpan.MaxValue.Ticks)
        {
            return TimeSpan.MaxValue;
        }

        return TimeSpan.FromTicks((long)Math.Round(ticks, MidpointRounding.AwayFromZero));
    }
}
