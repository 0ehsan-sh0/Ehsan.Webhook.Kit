namespace WebhookKit.Abstractions;

/// <summary>Creates the identifier for one inbound HTTP transmission.</summary>
public interface IWebhookIdGenerator
{
    /// <summary>Creates a new WebhookKit transmission identifier.</summary>
    /// <returns>A unique identifier in the format produced by the configured generator.</returns>
    string Create();
}
