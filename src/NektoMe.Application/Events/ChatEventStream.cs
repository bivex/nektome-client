namespace NektoMe.Application.Events;

/// <summary>
/// Minimal push stream for <see cref="ChatEvent"/>s. Consumers subscribe and
/// dispose the subscription when done — no reactive dependency required.
/// Events are published from transport threads; subscribers must marshal to
/// their own context (e.g. the UI dispatcher).
/// </summary>
public sealed class ChatEventStream
{
    private readonly object _gate = new();
    private event Action<ChatEvent>? Received;

    public IDisposable Subscribe(Action<ChatEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            Received += handler;
        }

        return new Subscription(this, handler);
    }

    internal void Publish(ChatEvent e)
    {
        Action<ChatEvent>? handlers;
        lock (_gate)
        {
            handlers = Received;
        }

        handlers?.Invoke(e);
    }

    private sealed class Subscription(ChatEventStream owner, Action<ChatEvent> handler) : IDisposable
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
