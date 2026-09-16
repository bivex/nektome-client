namespace NektoMe.Application.Events;

/// <summary>
/// Push stream for <see cref="VoiceChatEvent"/>s (same contract as the chat
/// stream: handlers run on transport threads, subscribers marshal themselves).
/// </summary>
public sealed class VoiceEventStream
{
    private readonly object _gate = new();
    private event Action<VoiceChatEvent>? Received;

    public IDisposable Subscribe(Action<VoiceChatEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            Received += handler;
        }

        return new Subscription(this, handler);
    }

    internal void Publish(VoiceChatEvent e)
    {
        Action<VoiceChatEvent>? handlers;
        lock (_gate)
        {
            handlers = Received;
        }

        handlers?.Invoke(e);
    }

    private sealed class Subscription(VoiceEventStream owner, Action<VoiceChatEvent> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner.Received -= handler;
            }
        }
    }
}
