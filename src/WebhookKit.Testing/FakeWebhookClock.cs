// Copyright (c) Ehsan. Licensed under the MIT License.
using WebhookKit.Abstractions;

namespace WebhookKit.Testing;

/// <summary>
/// Test-only mutable clock. Transient lifetime; never register as a singleton in production.
/// Thread-safe via lock.
/// </summary>
public sealed class FakeWebhookClock : IWebhookClock
{
    private readonly object _gate = new();
    private DateTimeOffset _utcNow;

    /// <summary>Create with an optional initial instant (defaults to current UTC).</summary>
    public FakeWebhookClock(DateTimeOffset? initial = null)
    {
        _utcNow = initial ?? DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }
        set
        {
            lock (_gate)
            {
                _utcNow = value;
            }
        }
    }

    /// <summary>Move the clock forward (or backward with a negative duration).</summary>
    public void Advance(TimeSpan duration)
    {
        lock (_gate)
        {
            _utcNow += duration;
        }
    }
}
