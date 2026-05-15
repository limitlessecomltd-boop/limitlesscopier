using FluentAssertions;
using LTC.Core.Logging;
using LTC.Core.Models;
using Xunit;

namespace LTC.Tests.Logging;

public class ActivityLogTests
{
    [Fact]
    public void Snapshot_NewestFirst()
    {
        var log = new ActivityLog();
        for (int i = 0; i < 5; i++)
            log.Append(new ActivityEntry { Kind = ActivityKind.Open, Symbol = $"SYM{i}" });

        var snap = log.Snapshot();
        snap.Should().HaveCount(5);
        snap[0].Symbol.Should().Be("SYM4");
        snap[4].Symbol.Should().Be("SYM0");
    }

    [Fact]
    public void RingBuffer_OverwritesOldest()
    {
        var log = new ActivityLog(capacity: 3);
        for (int i = 0; i < 5; i++)
            log.Append(new ActivityEntry { Kind = ActivityKind.Open, Symbol = $"SYM{i}" });

        var snap = log.Snapshot();
        snap.Should().HaveCount(3);
        snap.Select(e => e.Symbol).Should().Equal("SYM4", "SYM3", "SYM2");
    }

    [Fact]
    public async Task EntryChanged_FiresOnAppend()
    {
        var log = new ActivityLog();
        var tcs = new TaskCompletionSource<ActivityEntry>();
        log.EntryChanged += (_, e) => tcs.TrySetResult(e);

        var entry = new ActivityEntry { Kind = ActivityKind.Open, Symbol = "EURUSD" };
        log.Append(entry);

        var seen = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
        seen.Symbol.Should().Be("EURUSD");
    }

    [Fact]
    public async Task Update_FindsAndMutatesEntry()
    {
        var log = new ActivityLog();
        var entry = new ActivityEntry { Kind = ActivityKind.Open, Symbol = "EURUSD",
                                         Status = ActivityStatus.InFlight };
        log.Append(entry);

        // Drain initial EntryChanged to avoid ambiguity
        await Task.Delay(50);

        var seen = new TaskCompletionSource<ActivityEntry>();
        log.EntryChanged += (_, e) =>
        {
            if (e.Status == ActivityStatus.Success) seen.TrySetResult(e);
        };

        log.Update(entry.Id, e => { e.Status = ActivityStatus.Success; e.SlaveTicket = 999; });

        var updated = await seen.Task.WaitAsync(TimeSpan.FromSeconds(1));
        updated.Status.Should().Be(ActivityStatus.Success);
        updated.SlaveTicket.Should().Be(999UL);
    }

    [Fact]
    public async Task Sink_GetsCalled()
    {
        var log = new ActivityLog();
        var hit = new TaskCompletionSource<ActivityEntry>();
        log.Sink = e => hit.TrySetResult(e);

        log.Append(new ActivityEntry { Kind = ActivityKind.Open, Symbol = "X" });

        // Race the sink-completion task against a 5s timer; under heavy parallel load
        // the dispatcher can take longer than 1s to drain. 5s is still tight for a
        // sanity check while being forgiving on slow CI / VM machines.
        var winner = await Task.WhenAny(hit.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        winner.Should().Be(hit.Task);
    }
}
