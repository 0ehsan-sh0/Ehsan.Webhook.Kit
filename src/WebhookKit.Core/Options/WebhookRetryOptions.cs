// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Core.Options;

/// <summary>Retry settings shape. Engine lands with background processing (Task 22).</summary>
public sealed class WebhookRetryOptions
{
    /// <summary>Maximum processing attempts including the first. Default 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Delay before the first retry. Default 5 seconds.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Exponential multiplier per attempt. Default 2.</summary>
    public double BackoffMultiplier { get; set; } = 2;

    /// <summary>Apply jitter to avoid synchronized retries. Default true.</summary>
    public bool UseJitter { get; set; } = true;
}
