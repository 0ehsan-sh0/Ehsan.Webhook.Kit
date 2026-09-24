namespace WebhookKit.Core.Options;

/// <summary>Controls background dequeue, recovery, and processing leases.</summary>
public sealed class WebhookBackgroundOptions
{
    /// <summary>Whether the background worker is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Number of deliveries processed concurrently by the worker.</summary>
    public int WorkerConcurrency { get; set; } = 1;

    /// <summary>Interval between scans for recoverable persisted deliveries.</summary>
    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum records requested during one recovery scan.</summary>
    public int RecoveryBatchSize { get; set; } = 100;

    /// <summary>Duration of a processing lease acquired by the worker.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Minimum age of an expired lease before recovery considers it.</summary>
    public TimeSpan RecoveryAge { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time allowed for active processor tasks to finish during shutdown.</summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
