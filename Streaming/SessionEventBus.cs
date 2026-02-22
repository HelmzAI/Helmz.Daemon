using System.Collections.Concurrent;
using System.Threading.Channels;
using Helmz.Spec.V1;

namespace Helmz.Daemon.Streaming;

/// <summary>
/// Pub/sub event bus for session output and action requests.
/// Uses System.Threading.Channels for backpressure-aware streaming.
/// Multiple subscribers can subscribe to the same session.
/// Subscribers are keyed by ID so they can be removed when the gRPC stream ends.
/// </summary>
internal sealed class SessionEventBus
{
    private readonly ConcurrentDictionary<string, ChannelCollection> _channels = new(StringComparer.Ordinal);

    private const int DefaultCapacity = 1000;

    /// <summary>
    /// Subscribe to output chunks for a session.
    /// Returns a subscription that MUST be disposed when the consumer disconnects.
    /// </summary>
    public EventSubscription<OutputChunk> SubscribeOutput(string sessionId)
    {
        ChannelCollection collection = GetOrCreateChannels(sessionId);
        string id = Guid.NewGuid().ToString("N");
        Channel<OutputChunk> channel = Channel.CreateBounded<OutputChunk>(new BoundedChannelOptions(DefaultCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        collection.OutputSubscribers[id] = channel;

        return new EventSubscription<OutputChunk>(channel.Reader, () =>
        {
            if (collection.OutputSubscribers.TryRemove(id, out Channel<OutputChunk>? removed))
            {
                _ = removed.Writer.TryComplete();
            }
        });
    }

    /// <summary>
    /// Subscribe to action requests for a session.
    /// Returns a subscription that MUST be disposed when the consumer disconnects.
    /// </summary>
    public EventSubscription<ActionRequest> SubscribeActions(string sessionId)
    {
        ChannelCollection collection = GetOrCreateChannels(sessionId);
        string id = Guid.NewGuid().ToString("N");
        Channel<ActionRequest> channel = Channel.CreateBounded<ActionRequest>(new BoundedChannelOptions(DefaultCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        collection.ActionSubscribers[id] = channel;

        return new EventSubscription<ActionRequest>(channel.Reader, () =>
        {
            if (collection.ActionSubscribers.TryRemove(id, out Channel<ActionRequest>? removed))
            {
                _ = removed.Writer.TryComplete();
            }
        });
    }

    /// <summary>Publish an output chunk to all subscribers for a session.</summary>
    public ValueTask PublishOutputAsync(string sessionId, OutputChunk chunk)
    {
        if (!_channels.TryGetValue(sessionId, out ChannelCollection? collection))
        {
            return ValueTask.CompletedTask;
        }

        foreach ((string _, Channel<OutputChunk>? channel) in collection.OutputSubscribers)
        {
            // DropOldest mode: TryWrite always returns true (drops oldest item to make
            // room) unless the channel has been completed. Completed channels are cleaned
            // up by EventSubscription.Dispose() when the gRPC stream ends — no action
            // needed here.
            _ = channel.Writer.TryWrite(chunk);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Publish an action request to all subscribers for a session.</summary>
    public ValueTask PublishActionAsync(string sessionId, ActionRequest action)
    {
        if (!_channels.TryGetValue(sessionId, out ChannelCollection? collection))
        {
            return ValueTask.CompletedTask;
        }

        foreach ((string _, Channel<ActionRequest>? channel) in collection.ActionSubscribers)
        {
            // Wait mode: TryWrite returns false only if the channel is completed.
            // Completed channels are cleaned up by EventSubscription.Dispose().
            _ = channel.Writer.TryWrite(action);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Complete all channels for a session (signals end-of-stream to all subscribers).</summary>
    public void CompleteSession(string sessionId)
    {
        if (!_channels.TryRemove(sessionId, out ChannelCollection? collection))
        {
            return;
        }

        foreach ((string _, Channel<OutputChunk>? channel) in collection.OutputSubscribers)
        {
            _ = channel.Writer.TryComplete();
        }

        foreach ((string _, Channel<ActionRequest>? channel) in collection.ActionSubscribers)
        {
            _ = channel.Writer.TryComplete();
        }
    }

    private ChannelCollection GetOrCreateChannels(string sessionId)
    {
        return _channels.GetOrAdd(sessionId, _ => new ChannelCollection());
    }

    /// <summary>Holds all subscriber channels for a single session.</summary>
    private sealed class ChannelCollection
    {
        public ConcurrentDictionary<string, Channel<OutputChunk>> OutputSubscribers { get; } = new();
        public ConcurrentDictionary<string, Channel<ActionRequest>> ActionSubscribers { get; } = new();
    }
}

/// <summary>
/// A subscription to an event bus channel. Dispose to unsubscribe and clean up.
/// </summary>
internal sealed class EventSubscription<T>(ChannelReader<T> reader, Action onDispose) : IDisposable
{
    private readonly Action _onDispose = onDispose;
    private bool _disposed;

    public ChannelReader<T> Reader { get; } = reader;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _onDispose();
    }
}
