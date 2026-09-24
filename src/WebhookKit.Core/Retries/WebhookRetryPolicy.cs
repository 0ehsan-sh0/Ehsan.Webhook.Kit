using WebhookKit.Core.Options;

namespace WebhookKit.Core.Retries;

public interface IWebhookRetryDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public sealed class TaskWebhookRetryDelay : IWebhookRetryDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return delay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(delay, cancellationToken);
    }
}

public sealed class WebhookRetryPolicy
{
    private readonly Func<double> _jitterFactorProvider;

    public WebhookRetryPolicy(Func<double>? jitterFactorProvider = null)
    {
        _jitterFactorProvider = jitterFactorProvider ?? Random.Shared.NextDouble;
    }

    public double GetJitterFactor()
    {
        var factor = _jitterFactorProvider();
        if (double.IsNaN(factor) || double.IsInfinity(factor) || factor < 0 || factor > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        return factor;
    }

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

    public static TimeSpan CalculateMaximumRetryWindow(WebhookRetryOptions options)
    {
        return GetMaximumRetryWindow(options);
    }

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
