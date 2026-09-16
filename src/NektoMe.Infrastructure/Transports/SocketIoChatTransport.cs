using System.Text.Json;
using NektoMe.Application;
using NektoMe.Application.Abstractions;
using NektoMe.Application.Protocol;
using SocketIOClient;
using SocketIOClient.Common;

namespace NektoMe.Infrastructure.Transports;

/// <summary>Transport-specific knobs for <see cref="SocketIoChatTransport"/>.</summary>
public sealed class SocketIoTransportOptions
{
    /// <summary>
    /// The Android reference client uses socket.io-client 1.x (socket.io v2 wire
    /// protocol = engine.io v3), so that is the default.
    /// </summary>
    public EngineIO Engine { get; set; } = EngineIO.V3;

    public bool Reconnection { get; set; } = true;
}

/// <summary>
/// Socket.IO adapter over WebSocket for the NektoMe gateway: emits the
/// "action" event and surfaces the "notice" event as parsed envelopes.
/// </summary>
public sealed class SocketIoChatTransport : IChatTransport
{
    private readonly SocketIO _socket;

    public SocketIoChatTransport(ChatOptions options, SocketIoTransportOptions transportOptions)
    {
        _socket = new SocketIO(new Uri(options.ServerUrl), new SocketIOOptions
        {
            Path = WireNames.SocketPath,
            Transport = TransportProtocol.WebSocket,
            EIO = transportOptions.Engine,
            Reconnection = transportOptions.Reconnection,
        });

        _socket.OnConnected += (_, _) => Connected?.Invoke();
        _socket.OnDisconnected += (_, reason) => Disconnected?.Invoke(reason);
        _socket.OnReconnectAttempt += (_, _) => Reconnecting?.Invoke();
        _socket.OnError += (_, message) => TransportError?.Invoke(message);
        _socket.On(WireNames.NoticeEvent, OnNotice);
    }

    public event Action? Connected;
    public event Action<string?>? Disconnected;
    public event Action? Reconnecting;
    public event Action<string>? TransportError;
    public event Action<NoticeMessage>? NoticeReceived;

    public bool IsConnected => _socket.Connected;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => _socket.ConnectAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => _socket.DisconnectAsync(cancellationToken);

    public async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        // Socket.IO arguments are an array; the action object is the single argument.
        await _socket.EmitAsync(WireNames.EmitEvent, new object[] { message.Payload }).WaitAsync(cancellationToken);
    }

    private Task OnNotice(IEventContext response)
    {
        try
        {
            JsonElement root = response.GetValue<JsonElement>(0);
            NoticeEnvelope envelope = ProtocolCodec.ParseEnvelope(root);
            if (envelope.Notice is { } name)
            {
                NoticeReceived?.Invoke(new NoticeMessage(name, envelope.Data, envelope.Badge, envelope.Page));
            }
        }
        catch (Exception ex)
        {
            TransportError?.Invoke("Failed to parse notice: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    public void Dispose() => _socket.Dispose();
}
