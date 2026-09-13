// Copyright (c) Ehsan. Licensed under the MIT License.
using WebhookKit.Abstractions;

namespace WebhookKit.Core.Clocks;

/// <summary>Production clock backed by <see cref="DateTimeOffset.UtcNow"/>. Singleton-safe and thread-safe.</summary>
public sealed class SystemWebhookClock : IWebhookClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
