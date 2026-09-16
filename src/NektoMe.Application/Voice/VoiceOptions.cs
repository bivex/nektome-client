using NektoMe.Application.Abstractions;
using NektoMe.Application.Protocol;

using System.Globalization;

public enum VoiceProtocolMode
{
    /// <summary>Android app protocol: Socket.IO v4 over Engine.IO at audio.nekto.me with OkHttp headers.</summary>
    Android,

    /// <summary>Web browser protocol: raw WebSocket at audiochat.nekto.me with flat JSON and browser fingerprint/challenge.</summary>
    Web
}

/// <summary>
/// Voice chat roulette configuration. Defaults mirror the reference voice app's
/// live settings (Firebase Remote Config path <c>/androiduk</c> over the bundled
/// default URL <c>https://audio.nekto.me/</c>; signaling protocol version 2).
/// </summary>
public sealed class VoiceOptions
{
    /// <summary>Signaling protocol mode: Android (Socket.IO) vs Web (Raw WebSocket).</summary>
    public VoiceProtocolMode ProtocolMode { get; set; } = VoiceProtocolMode.Android;

    /// <summary>Web signaling endpoint (used when ProtocolMode == Web).</summary>
    public AudioServerEndpoint WebEndpoint { get; set; } =
        new(new Uri("https://audio.nekto.me/"), "/websocket");

    /// <summary>
    /// Fixed signaling endpoint. When set, the HTTP bootstrap is skipped — the
    /// reference app stopped publishing <c>nekto.me/audioserver.json</c> and now
    /// ships the address through Firebase Remote Config.
    /// </summary>
    public AudioServerEndpoint? StaticEndpoint { get; set; } =
        new(new Uri("https://audio.nekto.me/"), "/androiduk");

    /// <summary>HTTP document that publishes the signaling socket URL and path (legacy bootstrap).</summary>
    public string? BootstrapUrl { get; init; } = "https://nekto.me/audioserver.json";

    /// <summary>Used when the bootstrap document is unreachable (offline overrides, tests).</summary>
    public AudioServerEndpoint? FallbackEndpoint { get; init; }

    /// <summary>Handshake header value for <c>NektoMe-Chat-Version</c>.</summary>
    public string ChatVersion { get; init; } = "2";

    /// <summary>
    /// Two-letter locale sent with <c>register</c> and as the Android-Language
    /// handshake header. Defaults to the machine's UI language, mirroring the
    /// reference client's Locale.getDefault().getLanguage().
    /// </summary>
    public string Locale { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    /// <summary>IANA time zone id; defaults to the machine's zone when null.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Search criteria applied on every <c>scan-for-peer</c>.</summary>
    public VoiceSearchCriteria Search { get; init; } = new();

    /// <summary>Optional path for a WAV file receiving the remote peer's audio.</summary>
    public string? RemoteAudioDumpPath { get; init; }

    /// <summary>Timeout in seconds to establish WebRTC media after peer-connect before auto-reconnecting (default: 20s).</summary>
    public int PeerConnectTimeoutSeconds { get; set; } = 20;
}
