using System.Collections.Concurrent;
using System.Threading.Channels;
using Helmz.Spec.V1;

namespace Helmz.Daemon.Streaming;

/// <summary>
/// Pub/sub event bus for session output and action requests.
/// Uses System.Threading.Channels for backpressure-aware streaming.
/// Multiple subscribers can subscribe to the same session.
/// </summary>
internal sealed class SessionEventBus
{
    private readonly ConcurrentDictionary<string, ChannelCollection> _channels = new(StringComparer.Ordinal);

    private const int DefaultCapacity = 1000;

    /// <summary>Subscribe to output chunks for a session. Returns a reader to consume from.</summary>
    public ChannelReader<OutputChunk> SubscribeOutput(string sessionId)
    {
        var collection = GetOrCreateChannels(sessionId);
        var channel = Channel.CreateBounded<OutputChunk>(new BoundedChannelOptions(DefaultCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        collection.OutputSubscribers.Add(channel);
        return channel.Reader;
    }

    /// <summary>Subscribe to action requests for a session. Returns a reader to consume from.</summary>
    public ChannelReader<ActionRequest> SubscribeActions(string sessionId)
    {
        var collection = GetOrCreateChannels(sessionId);
        var channel = Channel.CreateBounded<ActionRequest>(new BoundedChannelOptions(DefaultCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        collection.ActionSubscribers.Add(channel);
        return channel.Reader;
    }

    /// <summary>Publish an output chunk to all subscribers for a session.</summary>
    public async ValueTask PublishOutputAsync(string sessionId, OutputChunk chunk, CancellationToken cancellationToken = default)
    {
        if (!_channels.TryGetValue(sessionId, out var collection))
        {
            return;
        }

        foreach (var channel in collection.OutputSubscribers)
        {
            // TryWrite + DropOldest ensures we never block on slow consumers
            await channel.Writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Publish an action request to all subscribers for a session.</summary>
    public async ValueTask PublishActionAsync(string sessionId, ActionRequest action, CancellationToken cancellationToken = default)
    {
        if (!_channels.TryGetValue(sessionId, out var collection))
        {
            return;
        }

        foreach (var channel in collection.ActionSubscribers)
        {
            await channel.Writer.WriteAsync(action, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Complete all channels for a session (signals end-of-stream to all subscribers).</summary>
    public void CompleteSession(string sessionId)
    {
        if (!_channels.TryRemove(sessionId, out var collection))
        {
            return;
        }

        foreach (var channel in collection.OutputSubscribers)
        {
            channel.Writer.TryComplete();
        }

        foreach (var channel in collection.ActionSubscribers)
        {
            channel.Writer.TryComplete();
        }
    }

    private ChannelCollection GetOrCreateChannels(string sessionId)
    {
        return _channels.GetOrAdd(sessionId, _ => new ChannelCollection());
    }

    /// <summary>Holds all subscriber channels for a single session.</summary>
    private sealed class ChannelCollection
    {
        public ConcurrentBag<Channel<OutputChunk>> OutputSubscribers { get; } = [];
        public ConcurrentBag<Channel<ActionRequest>> ActionSubscribers { get; } = [];
    }
}
