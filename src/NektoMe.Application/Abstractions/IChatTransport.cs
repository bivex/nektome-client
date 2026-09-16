using System.Text.Json;

namespace NektoMe.Application.Abstractions;

/// <summary>
/// Wire-level outbound message. <see cref="Payload"/> is the complete action
/// object (including the "action" name field); the transport never interprets it.
/// </summary>
public sealed record OutboundMessage(JsonElement Payload);

/// <summary>Wire-level notification received from the server.</summary>
public sealed record NoticeMessage(string Name, JsonElement? Data, int? Badge, int? Page);

/// <summary>
/// Driven port: a bidirectional message mover. Implementations handle the
/// physical transport (Socket.IO, raw WebSocket, …) but never parse payloads.
/// </summary>
public interface IChatTransport : IDisposable
{
    /// <summary>Raised when the transport (re)established a session.</summary>
    event Action? Connected;

    /// <summary>Raised when the session ended; carries the transport-level reason.</summary>
    event Action<string?>? Disconnected;

    /// <summary>Raised when an automatic reconnect attempt starts.</summary>
    event Action? Reconnecting;

    /// <summary>Raised for transport-level failures that are not disconnections.</summary>
    event Action<string>? TransportError;

    /// <summary>Raised for every server notification.</summary>
    event Action<NoticeMessage>? NoticeReceived;

    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default);
}
