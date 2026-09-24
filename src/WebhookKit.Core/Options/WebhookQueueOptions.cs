namespace WebhookKit.Core.Options;

/// <summary>Capacity settings for the in-process notification queue.</summary>
public sealed class WebhookQueueOptions
{
    /// <summary>Default number of work items the queue can hold.</summary>
    public const int DefaultCapacity = 1024;

    /// <summary>Maximum queue capacity; must be greater than zero.</summary>
    public int Capacity { get; set; } = DefaultCapacity;
}
