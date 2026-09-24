// Copyright (c) Ehsan. Licensed under the MIT License.
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using WebhookKit.Abstractions;
using WebhookKit.Core.Clocks;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Testing;

namespace WebhookKit.Core.Tests;

public sealed class ClockTests
{
    [Fact]
    public void SystemClock_IsCloseToUtcNow()
    {
        var clock = new SystemWebhookClock();

        var before = DateTimeOffset.UtcNow;
        var now = clock.UtcNow;
        var after = DateTimeOffset.UtcNow;

        now.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void FakeClock_IsSettable_AndAdvances()
    {
        var start = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeWebhookClock(start);

        clock.UtcNow.Should().Be(start);

        clock.Advance(TimeSpan.FromMinutes(5));
        clock.UtcNow.Should().Be(start.AddMinutes(5));

        var next = start.AddHours(1);
        clock.UtcNow = next;
        clock.UtcNow.Should().Be(next);
    }

    [Fact]
    public void AddWebhookKit_PreservesCustomClockRegisteredBeforeDefaults()
    {
        var customClock = new FakeWebhookClock(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<IWebhookClock>(customClock);

        services.AddWebhookKit();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IWebhookClock>().Should().BeSameAs(customClock);
    }

    [Fact]
    public void FakeClock_SupportsToleranceBoundary()
    {
        var start = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeWebhookClock(start);
        var tolerance = TimeSpan.FromMinutes(5);

        clock.Advance(tolerance);
        (clock.UtcNow - start).Should().Be(tolerance);
    }
}
