namespace NektoMe.Application.Abstractions;

/// <summary>An ICE server handed to us by the server (ephemeral TURN credentials).</summary>
public sealed record VoiceIceServer(string Url, string? Username, string? Credential);

/// <summary>An SDP-level ICE candidate, transport agnostic.</summary>
public sealed record VoiceIceCandidate(string SdpMid, int SdpMLineIndex, string Candidate);

/// <summary>
/// Driven port for the WebRTC audio leg of a voice session. One instance
/// covers one peer connection; the session creates a fresh one per
/// <c>peer-connect</c>. Engine sends a continuous audio stream (silence unless
/// microphone PCM is pushed in) and forwards decoded remote audio to a sink.
/// </summary>
public interface IAudioEngine : IDisposable
{
    /// <summary>Raised for each local ICE candidate gathered.</summary>
    event Action<VoiceIceCandidate>? LocalIceCandidate;

    /// <summary>Raised once the remote audio track is flowing.</summary>
    event Action? RemoteAudioStarted;

    /// <summary>Raised on connection-state changes ("new", "checking", "connected", …).</summary>
    event Action<string>? StateChanged;

    /// <summary>Raised on ICE connection-state changes ("new", "checking", "connected", "completed", …).</summary>
    event Action<string>? IceStateChanged;

    /// <summary>Raised for fatal media-leg errors.</summary>
    event Action<string>? Failed;

    /// <summary>Raised for non-fatal media-leg diagnostics (ICE gathering, candidates, RTP).</summary>
    event Action<string>? Diagnostic;

    /// <summary>Builds the peer connection. <paramref name="relayOnly"/> forces all media through TURN.</summary>
    Task CreateConnectionAsync(
        IReadOnlyList<VoiceIceServer> iceServers,
        bool relayOnly,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a local offer; returns the SDP body (without the JSON envelope).</summary>
    Task<string> CreateOfferAsync();

    /// <summary>Applies the remote description ("offer" or "answer" SDP body).</summary>
    Task SetRemoteDescriptionAsync(string type, string sdp);

    /// <summary>Creates a local answer for the remote offer; returns the SDP body.</summary>
    Task<string> CreateAnswerAsync();

    /// <summary>Feeds a remote ICE candidate (call only after the remote description is set).</summary>
    Task AddRemoteIceCandidateAsync(VoiceIceCandidate candidate);

    /// <summary>Stops/resumes sending our audio.</summary>
    void SetMuted(bool muted);

    /// <summary>Optional live microphone input: 16-bit signed PCM, mono.</summary>
    void PushMicrophonePcm(short[] samples, int sampleRate);

    void Close();
}

/// <summary>Consumes decoded remote audio as 16-bit signed PCM.</summary>
public interface IAudioSink
{
    void Write(short[] pcm, int sampleRate);
}
