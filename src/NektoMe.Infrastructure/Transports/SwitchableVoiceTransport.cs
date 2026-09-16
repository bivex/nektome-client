using Microsoft.Extensions.Logging;
using NektoMe.Application;
using NektoMe.Application.Abstractions;

namespace NektoMe.Infrastructure.Transports;

/// <summary>
/// Composite voice transport that switches between Android (Socket.IO over Engine.IO)
/// and Web (raw WebSocket with flat JSON) based on <see cref="VoiceOptions.ProtocolMode"/>.
/// </summary>
public sealed class SwitchableVoiceTransport : IVoiceTransport
{
    private readonly VoiceOptions _options;
    private readonly SocketIoVoiceTransport _androidTransport;
    private readonly SocketIoVoiceTransport _webTransport;

    public IVoiceTransport ActiveTransport =>
        _options.ProtocolMode == VoiceProtocolMode.Web ? _webTransport : _androidTransport;

    public SwitchableVoiceTransport(
        VoiceOptions options,
        VoiceTransportOptions? transportOptions = null,
        ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _androidTransport = new SocketIoVoiceTransport(options, transportOptions);
        _webTransport = new SocketIoVoiceTransport(options, transportOptions);

        WireTransport(_androidTransport);
        WireTransport(_webTransport);
    }

    private void WireTransport(IVoiceTransport transport)
    {
        transport.Connected += () =>
        {
            if (ReferenceEquals(ActiveTransport, transport))
                Connected?.Invoke();
        };

        transport.Disconnected += reason =>
        {
            if (ReferenceEquals(ActiveTransport, transport))
                Disconnected?.Invoke(reason);
        };

        transport.Reconnecting += () =>
        {
            if (ReferenceEquals(ActiveTransport, transport))
                Reconnecting?.Invoke();
        };

        transport.TransportError += err =>
        {
            if (ReferenceEquals(ActiveTransport, transport))
                TransportError?.Invoke(err);
        };

        transport.EventReceived += ev =>
        {
            if (ReferenceEquals(ActiveTransport, transport))
                EventReceived?.Invoke(ev);
        };
    }

    public event Action? Connected;
    public event Action<string?>? Disconnected;
    public event Action? Reconnecting;
    public event Action<string>? TransportError;
    public event Action<VoiceWireEvent>? EventReceived;

    public bool IsConnected => ActiveTransport.IsConnected;

    public async Task ConnectAsync(AudioServerEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        if (_options.ProtocolMode == VoiceProtocolMode.Web)
        {
            if (_androidTransport.IsConnected)
                await _androidTransport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            await _webTransport.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (_webTransport.IsConnected)
                await _webTransport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            await _androidTransport.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _androidTransport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        await _webTransport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SendAsync(VoiceWireEvent message, CancellationToken cancellationToken = default) =>
        ActiveTransport.SendAsync(message, cancellationToken);

    public void Dispose()
    {
        _androidTransport.Dispose();
        _webTransport.Dispose();
    }
}
