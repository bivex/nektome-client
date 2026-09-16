using System.Text.Json;

namespace NektoMe.Application.Abstractions;

/// <summary>
/// A flat voice-signaling event as it travels the wire: <c>{"type":"&lt;name&gt;", …}</c>.
/// <see cref="Root"/> is the complete JSON object; <see cref="Type"/> is the
/// server's event discriminator.
/// </summary>
public sealed record VoiceWireEvent(string Type, JsonElement Root);

/// <summary>
/// Driven port for the voice-app signaling socket: a single Socket.IO channel
/// carrying the flat <c>{"type": …}</c> envelope in both directions.
/// Implementations never interpret payloads.
/// </summary>
public interface IVoiceTransport : IDisposable
{
    /// <summary>Raised when the socket (re)established a session.</summary>
    event Action? Connected;

    /// <summary>Raised when the session ended; carries the transport-level reason.</summary>
    event Action<string?>? Disconnected;

    /// <summary>Raised when an automatic reconnect attempt starts.</summary>
    event Action? Reconnecting;

    /// <summary>Raised for transport-level failures that are not disconnections.</summary>
    event Action<string>? TransportError;

    /// <summary>Raised for every signaling event received on the channel.</summary>
    event Action<VoiceWireEvent>? EventReceived;

    bool IsConnected { get; }

    /// <summary>Connects using the server endpoint resolved at call time.</summary>
    Task ConnectAsync(AudioServerEndpoint endpoint, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task SendAsync(VoiceWireEvent message, CancellationToken cancellationToken = default);
}
