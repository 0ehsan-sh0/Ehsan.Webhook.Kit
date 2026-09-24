namespace WebhookKit.Core.Options;

public sealed class WebhookQueueOptions
{
    public const int DefaultCapacity = 1024;

    public int Capacity { get; set; } = DefaultCapacity;
}
