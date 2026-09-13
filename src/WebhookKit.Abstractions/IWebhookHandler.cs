// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Strongly typed application handler for a deserialized event payload.
/// </summary>
/// <typeparam name="TEvent">Event payload type.</typeparam>
public interface IWebhookHandler<in TEvent>
{
    /// <summary>Handle the deserialized event with its execution context.</summary>
    Task HandleAsync(TEvent eventData, WebhookContext context, CancellationToken cancellationToken = default);
}
