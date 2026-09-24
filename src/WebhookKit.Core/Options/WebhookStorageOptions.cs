namespace WebhookKit.Core.Options;

/// <summary>Controls whether exact request bodies are retained by configured stores.</summary>
public sealed class WebhookStorageOptions
{
    /// <summary>Whether stores retain the exact raw body for verification and recovery.</summary>
    public bool PersistRawBody { get; set; } = true;

    /// <summary>Whether a successful synchronous delivery may discard its retained raw body.</summary>
    public bool DiscardRawBodyAfterSuccessfulSync { get; set; }
}
