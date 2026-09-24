// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Strongly typed application handler for a deserialized event payload.
/// </summary>
/// <typeparam name="TEvent">Event payload type.</typeparam>
public interface IWebhookHandler<in TEvent>
{
    /// <summary>Handle the deserialized event with its execution context.</summary>
    /// <param name="eventData">The deserialized event payload.</param>
    /// <param name="context">Verified metadata and payload access for this delivery.</param>
    /// <param name="cancellationToken">Token used to cancel application handling.</param>
    /// <returns>A task that represents handler execution.</returns>
    Task HandleAsync(TEvent eventData, WebhookContext context, CancellationToken cancellationToken = default);
}
