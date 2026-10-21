using System.Threading.Channels;

namespace SlothBox.Server.Events;

// One event of spec 8.3 (or the worker's own `worker.changed`), with its payload DTO.
public sealed record ServerEvent(string Name, object Payload);

// In-process fan-out: the worker publishes, each `/events` connection reads its own subscription.
public sealed class ServerEvents
{
    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    public sealed class Subscription : IDisposable
    {
        private readonly ServerEvents _owner;

        // A reader that falls behind loses its oldest events instead of growing without bound.
        private readonly Channel<ServerEvent> _channel = Channel.CreateBounded<ServerEvent>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        internal Subscription(ServerEvents owner) => _owner = owner;

        public ChannelReader<ServerEvent> Events => _channel.Reader;

        internal void Deliver(ServerEvent serverEvent) => _channel.Writer.TryWrite(serverEvent);

        public void Dispose()
        {
            _owner.Remove(this);
            _channel.Writer.TryComplete();
        }
    }

    public Subscription Subscribe()
    {
        var subscription = new Subscription(this);
        lock (_gate)
            _subscriptions.Add(subscription);
        return subscription;
    }

    public void Publish(ServerEvent serverEvent)
    {
        Subscription[] targets;
        lock (_gate)
            targets = [.. _subscriptions];
        foreach (var target in targets)
            target.Deliver(serverEvent);
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
            _subscriptions.Remove(subscription);
    }
}
