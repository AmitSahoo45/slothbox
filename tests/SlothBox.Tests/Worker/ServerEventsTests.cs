using SlothBox.Server.Api;
using SlothBox.Server.Events;

namespace SlothBox.Tests.Worker;

public sealed class ServerEventsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_subscriber_receives_a_published_event()
    {
        var events = new ServerEvents();
        using var first = events.Subscribe();
        using var second = events.Subscribe();
        var queue = new ServerEvent("queue.changed", new QueueChangedEvent(3, 1));

        events.Publish(queue);

        (await first.Events.ReadAsync(Ct)).ShouldBe(queue);
        (await second.Events.ReadAsync(Ct)).ShouldBe(queue);
    }

    [Fact]
    public async Task A_disposed_subscription_receives_nothing_more()
    {
        var events = new ServerEvents();
        var subscription = events.Subscribe();

        subscription.Dispose();
        events.Publish(new ServerEvent("queue.changed", new QueueChangedEvent(0, 0)));

        subscription.Events.TryRead(out _).ShouldBeFalse();
        await subscription.Events.Completion;
    }

    [Fact]
    public void Publishing_with_no_subscribers_is_fine() =>
        new ServerEvents().Publish(new ServerEvent("queue.changed", new QueueChangedEvent(0, 0)));
}
