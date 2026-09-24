// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Core.Options;

/// <summary>Retry settings shape.</summary>
public sealed class WebhookRetryOptions
{
    /// <summary>Maximum processing attempts including the first. Default 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Delay before the first retry. Default 2 seconds.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Exponential multiplier per attempt. Default 2.</summary>
    public double BackoffMultiplier { get; set; } = 2;

    /// <summary>Apply jitter to avoid synchronized retries. Default true.</summary>
    public bool UseJitter { get; set; } = true;

    /// <summary>Maximum fractional increase applied by jitter, from zero through one.</summary>
    public double JitterRatio { get; set; } = 0.2;
}
