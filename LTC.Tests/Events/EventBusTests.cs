using FluentAssertions;
using LTC.Core.Connections;
using LTC.Core.Events;
using Xunit;

namespace LTC.Tests.Events;

public class EventBusTests
{
    private static MasterOrderEvent MakeEvent(ulong ticket = 123) => new(
        MasterAccountId: Guid.NewGuid(),
        Kind: MasterEventKind.MarketOpen,
        Ticket: ticket,
        Symbol: "EURUSD",
        OrderType: CopyOrderType.Buy,
        Volume: 0.1,
        Price: 1.1,
        StopLoss: 0,
        TakeProfit: 0,
        ServerTimeUtc: DateTime.UtcNow,
        ReceivedAtTicks: 0);

    [Fact]
    public async Task Publish_DeliversToHandler()
    {
        await using var bus = new EventBus();
        var seen = new List<ulong>();
        var done = new TaskCompletionSource();
        bus.SetHandler(ev =>
        {
            lock (seen) seen.Add(ev.Ticket);
            if (seen.Count == 3) done.TrySetResult();
            return ValueTask.CompletedTask;
        });

        bus.Publish(MakeEvent(1));
        bus.Publish(MakeEvent(2));
        bus.Publish(MakeEvent(3));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        seen.Should().BeEquivalentTo(new ulong[] { 1, 2, 3 });
    }

    [Fact]
    public async Task HandlerException_DoesNotKillDispatcher()
    {
        await using var bus = new EventBus();
        int processed = 0;
        var done = new TaskCompletionSource();
        bus.SetHandler(ev =>
        {
            int n = Interlocked.Increment(ref processed);
            if (n == 1) throw new InvalidOperationException("boom");
            if (n == 2) done.TrySetResult();
            return ValueTask.CompletedTask;
        });

        bus.Publish(MakeEvent(1));
        bus.Publish(MakeEvent(2));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        processed.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public void SetHandler_TwiceThrows()
    {
        var bus = new EventBus();
        bus.SetHandler(_ => ValueTask.CompletedTask);
        Action act = () => bus.SetHandler(_ => ValueTask.CompletedTask);
        act.Should().Throw<InvalidOperationException>();
    }
}
