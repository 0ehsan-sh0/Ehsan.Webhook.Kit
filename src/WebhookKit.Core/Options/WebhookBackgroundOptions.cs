namespace WebhookKit.Core.Options;

public sealed class WebhookBackgroundOptions
{
    public bool Enabled { get; set; }

    public int WorkerConcurrency { get; set; } = 1;

    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(30);

    public int RecoveryBatchSize { get; set; } = 100;

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan RecoveryAge { get; set; } = TimeSpan.FromSeconds(30);
}
