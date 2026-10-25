namespace EpochAssist.Core;

/// <summary>A feature's subscription to one event type. The host ends it when the feature leaves Running, so Shutdown
/// never has to; Dispose ends it early.</summary>
internal sealed class Subscription : IDisposable
{
    private readonly Bus.Channel _channel;

    internal Subscription(Feature owner, Bus.Channel channel)
    {
        Owner = owner;
        _channel = channel;
    }

    public Feature Owner { get; }
    public Type Type => _channel.Type;
    public bool Active { get; private set; } = true;

    public void Dispose()
    {
        if (!Active) return;
        _channel.Remove(this);
    }

    internal void End() => Active = false;
}

/// <summary>
/// Typed events between features: core producers raise record structs, features subscribe with
/// themselves as the owner. Delivery is synchronous, in the frame of the raise, to the subscribers present when it
/// starts, each handler in its owner's guard, so a failure counts against that owner only. An event raised while
/// handlers run is queued and delivered after them, in order, in the same frame.
/// </summary>
internal static class Bus
{
    private const int MaxDepth = 4;
    private const int MaxQueued = 64;

    internal abstract class Channel
    {
        public abstract Type Type { get; }
        public abstract int Count { get; }
        public abstract void Remove(Subscription subscription);
        public abstract void RemoveOwner(Feature owner);
        public abstract IEnumerable<Subscription> Subscriptions { get; }
    }

    private sealed class Channel<T> : Channel where T : struct
    {
        public readonly List<(Subscription Subscription, Action<T> Handler)> Handlers = new();
        public readonly string Phase = "event " + typeof(T).Name;

        public override Type Type => typeof(T);
        public override int Count => Handlers.Count;
        public override IEnumerable<Subscription> Subscriptions => Handlers.Select(h => h.Subscription);

        public override void Remove(Subscription subscription)
        {
            subscription.End();
            Handlers.RemoveAll(h => h.Subscription == subscription);
        }

        public override void RemoveOwner(Feature owner)
        {
            for (int i = Handlers.Count - 1; i >= 0; i--)
            {
                if (Handlers[i].Subscription.Owner != owner) continue;
                Handlers[i].Subscription.End();
                Handlers.RemoveAt(i);
            }
        }
    }

    // One channel per event type, made on first use; the list lets the host remove an owner from all of them.
    private static class Of<T> where T : struct
    {
        public static Channel<T>? Channel;
    }

    private static readonly List<Channel> s_channels = new();
    // Raises from inside handlers, delivered once the current handlers finish, each with its depth.
    private static readonly Queue<(Action Deliver, int Depth)> s_queue = new();
    private static int s_depth, s_queuedThisRaise;

    /// <summary>Every channel with its subscriptions, for the dev `events subs`.</summary>
    internal static IReadOnlyList<Channel> Channels => s_channels;

    public static Subscription Subscribe<T>(Feature owner, Action<T> handler) where T : struct
    {
        var channel = ChannelOf<T>();
        var subscription = new Subscription(owner, channel);
        channel.Handlers.Add((subscription, handler));
        return subscription;
    }

    /// <summary>For producers: whether anything listens, so they can stay idle.</summary>
    public static bool HasSubscribers<T>() where T : struct => Of<T>.Channel is { Count: > 0 };

    /// <summary>Raised by core producers only.</summary>
    public static void Raise<T>(T e) where T : struct
    {
#if DEV
        Remember(e);
#endif
        if (Of<T>.Channel is not { Count: > 0 } channel) return;
        if (s_depth > 0)
        {
            if (s_depth >= MaxDepth || s_queuedThisRaise >= MaxQueued)
            {
                Log.ErrorThrottled("bus:overflow", $"Event {typeof(T).Name} dropped: {(s_depth >= MaxDepth ? $"raised more than {MaxDepth} deep" : $"over {MaxQueued} raised in one delivery")}", 30);
                return;
            }
            s_queuedThisRaise++;
            s_queue.Enqueue((() => Deliver(channel, e), s_depth + 1));
            return;
        }

        s_depth = 1;
        try
        {
            Deliver(channel, e);
            while (s_queue.Count > 0)
            {
                var (deliver, depth) = s_queue.Dequeue();
                s_depth = depth;
                deliver();
            }
        }
        finally
        {
            s_depth = 0;
            s_queuedThisRaise = 0;
            s_queue.Clear();
        }
    }

    // A snapshot, so a handler that subscribes or ends a subscription changes only later raises.
    private static void Deliver<T>(Channel<T> channel, T e) where T : struct
    {
        var handlers = channel.Handlers.ToArray();
        foreach (var (subscription, handler) in handlers)
        {
            if (!subscription.Active || subscription.Owner.State != FeatureState.Running) continue;
            FeatureHost.Guarded(subscription.Owner, channel.Phase, () => handler(e));
        }
    }

    /// <summary>Ends every subscription of the feature (the host, wherever it drops its jobs).</summary>
    internal static void RemoveOwner(Feature owner)
    {
        foreach (var channel in s_channels) channel.RemoveOwner(owner);
    }

    private static Channel<T> ChannelOf<T>() where T : struct
    {
        if (Of<T>.Channel is { } channel) return channel;
        channel = new Channel<T>();
        Of<T>.Channel = channel;
        s_channels.Add(channel);
        return channel;
    }

#if DEV
    private const int RecentCount = 200;
    private static readonly Queue<(DateTime At, string Type, string Text)> s_recent = new();

    /// <summary>Dev only: every event raised so far this load, and the last 200 of them.</summary>
    internal static int RaisedCount { get; private set; }
    internal static IEnumerable<(DateTime At, string Type, string Text)> Recent => s_recent;

    private static void Remember<T>(T e) where T : struct
    {
        RaisedCount++;
        s_recent.Enqueue((DateTime.Now, typeof(T).Name, e.ToString() ?? typeof(T).Name));
        while (s_recent.Count > RecentCount) s_recent.Dequeue();
    }
#endif
}
