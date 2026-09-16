using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NektoMe.Application.Abstractions;
using SIPSorcery.Net;
using SDPWellKnownMediaFormatsEnum = SIPSorceryMedia.Abstractions.SDPWellKnownMediaFormatsEnum;

namespace NektoMe.Infrastructure.Media;

#pragma warning disable CS0618 // Concentus obsolete warnings

/// <summary>
/// <see cref="IAudioEngine"/> backed by SIPSorcery's <see cref="RTCPeerConnection"/>.
/// Audio is negotiated PCMU-only and pumped as a continuous 20 ms stream: silence
/// unless the caller pushes microphone PCM, zeros while muted. Remote RTP is
/// decoded back to PCM and handed to the sink (optionally mirrored to a WAV file).
/// </summary>
public sealed class SipsorceryAudioEngine : IAudioEngine
{
    private const int FrameSamples = 160; // 20 ms @ 8 kHz

    /// <summary>Router that forwards SIPSorcery-internal warnings to the live engine instance.</summary>
    private static Action<string>? _warningRouter;

    static SipsorceryAudioEngine()
    {
        // SIPSorcery's own STUN/TURN/DTLS transaction logs are the only way to see
        // WHY gathering or connectivity checks fail; capture them to a file and
        // surface warnings through the active engine instance. Installed in the
        // static ctor so it precedes SIPSorcery's eagerly captured static loggers.
        try
        {
            SIPSorcery.LogFactory.Set(
                new SipsorcerySingleLoggerFactory(new SipsorceryFileLogger(OnInternalLog)));
        }
        catch (Exception)
        {
            // Logging is best effort; diagnostics degrade to candidate/state events.
        }
    }

    private static void OnInternalLog(string message)
    {
        _warningRouter?.Invoke(message);
    }

    private readonly ConcurrentQueue<short> _microphone = new();
    private readonly object _gate = new();
    private readonly IAudioSink? _sink;
    private readonly WavPcmWriter? _dump;
    private readonly Func<string?>? _activeProxyProvider;

    private RTCPeerConnection? _peerConnection;
    private TurnTcpForwarder? _forwarder;
    private bool _muted;
    private bool _remoteAudioStarted;
    private volatile bool _closed;

    private readonly Concentus.Structs.OpusEncoder _opusEncoder;
    private readonly Concentus.Structs.OpusDecoder _opusDecoder;
    private bool _useOpus;
    /// <summary>
    /// Signals when the ICE gathering phase (including relay candidate allocation)
    /// has completed.  CreateOfferAsync and CreateAnswerAsync await this so that
    /// the SDP they return contains embedded a=candidate lines for relay, allowing
    /// the remote peer to attempt relay→relay immediately without a costly ICE
    /// restart round-trip.
    /// </summary>
    private TaskCompletionSource<bool>? _gatheringComplete;
    /// <summary>
    /// Raw ICE candidate strings (the "candidate:..." part, without "a=") collected
    /// via <c>onicecandidate</c> during the current gathering phase.  Used to inject
    /// a=candidate lines into the outgoing offer SDP, because SIPSorcery uses trickle
    /// ICE and does NOT embed candidates in <c>localDescription.sdp</c>.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentBag<string> _gatheredCandidates = new();

    /// <param name="activeProxyProvider">
    /// Returns the currently active proxy URL ("http://user:pass@host:port" /
    /// "socks5://..."), when one is in use. When present, TURN media is tunnelled
    /// over TCP through it via a local forwarder — direct UDP to the TURN server
    /// is assumed unreachable.
    /// </param>
    public SipsorceryAudioEngine(
        IAudioSink? sink, string? remoteDumpPath = null, Func<string?>? activeProxyProvider = null)
    {
        _activeProxyProvider = activeProxyProvider;
        _sink = remoteDumpPath is null
            ? sink
            : new TeeSink(sink, new WavPcmWriter(remoteDumpPath));
        _dump = _sink as WavPcmWriter ?? (_sink is TeeSink tee ? tee.Dump : null);
        
        _opusEncoder = new Concentus.Structs.OpusEncoder(48000, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
        _opusEncoder.Bitrate = 32000;
        _opusDecoder = new Concentus.Structs.OpusDecoder(48000, 1);
    }

    /// <summary>Path of the remote-audio WAV dump, when enabled.</summary>
    public string? DumpPath => _dump?.FilePath;

    public event Action<VoiceIceCandidate>? LocalIceCandidate;

    public event Action? RemoteAudioStarted;

    public event Action<string>? StateChanged;

    public event Action<string>? IceStateChanged;

    public event Action<string>? Failed;

    /// <summary>Raised for non-fatal media-leg diagnostics (ICE gathering, candidates, RTP).</summary>
    public event Action<string>? Diagnostic;

    public async Task CreateConnectionAsync(
        IReadOnlyList<VoiceIceServer> iceServers, bool relayOnly, CancellationToken cancellationToken = default)
    {
        // Tunnel TURN through the proxy when one is active: the server's TURN
        // endpoints are otherwise unreachable (direct UDP/TCP is blackholed).
        if (_activeProxyProvider is not null)
        {
            _forwarder ??= new TurnTcpForwarder(_activeProxyProvider, message => Diagnostic?.Invoke(message));
            iceServers = _forwarder.Wrap(iceServers);

            // NEKTO PATCH (proxy warm-up): wait for all TCP tunnels to the TURN
            // servers to be established through the proxy *before* creating the
            // RTCPeerConnection.  SIPSorcery starts sending TURN Allocate requests
            // immediately after construction; if the tunnel is not ready yet, the
            // 25-request × 50 ms = 1.25 s budget expires before the proxy handshake
            // completes (~2 s over a residential proxy), and TURN gathering fails
            // silently — leaving us with no relay candidates.
            Diagnostic?.Invoke("форвардер TURN: ожидаем готовности TCP-туннелей...");
            await _forwarder.WarmUpAsync(cancellationToken).ConfigureAwait(false);
            Diagnostic?.Invoke("форвардер TURN: TCP-туннели готовы, запускаем ICE.");
        }

        var configIceServers = iceServers
            .Where(s => !string.IsNullOrWhiteSpace(s.Url))
            .Select(s => new RTCIceServer
            {
                urls = s.Url,
                username = s.Username,
                credential = s.Credential,
            })
            .ToList();

        // Always keep a public STUN fallback, appended last so server-provided
        // TURN/STUN are tried first: if the configured TURN is unreachable we
        // still stand a chance of gathering a server-reflexive candidate.
        if (!configIceServers.Any(s => s.urls.Contains("google.com", StringComparison.OrdinalIgnoreCase)))
        {
            configIceServers.Add(new RTCIceServer { urls = "stun:stun.l.google.com:19302" });
        }

        Diagnostic?.Invoke($"ICE: серверов={configIceServers.Count}, политика=all{(relayOnly ? " (сервер просил relay)" : "")}: " +
            string.Join(", ", configIceServers.Select(s => s.urls)));

        var config = new RTCConfiguration
        {
            iceServers = configIceServers,
            // Always allow host and STUN candidates (browser and python clients never restrict to relay-only)
            iceTransportPolicy = RTCIceTransportPolicy.all,
        };

        var pc = new RTCPeerConnection(config);
        var opusFormat = new SIPSorceryMedia.Abstractions.AudioFormat(111, "opus", 48000, 120, "2");
        // Prefer Opus for high quality, fallback to G.711 μ-law and A-law
        pc.addTrack(new MediaStreamTrack(new System.Collections.Generic.List<SIPSorceryMedia.Abstractions.AudioFormat> { opusFormat, new SIPSorceryMedia.Abstractions.AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU), new SIPSorceryMedia.Abstractions.AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA) }));

        // Surface SIPSorcery's own STUN/TURN warnings through this live instance.
        _warningRouter = message =>
        {
            try { Diagnostic?.Invoke(message); } catch { }
        };

        // Fresh gathering-complete signal for this connection attempt.
        // Re-created here so that repeated CreateConnectionAsync calls (peer restarts)
        // that reuse the same engine instance get a fresh signal.
        // Also clear the candidate bag so we only carry the current session's candidates.
        _gatheringComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        while (_gatheredCandidates.TryTake(out _)) { }

        pc.OnAudioFormatsNegotiated += (formats) =>
        {
            if (formats.Count > 0 && formats[0].FormatName.Equals("opus", StringComparison.OrdinalIgnoreCase))
            {
                Diagnostic?.Invoke("Аудиокодек согласован: Opus (48 kHz)");
                _useOpus = true;
            }
            else
            {
                Diagnostic?.Invoke($"Аудиокодек согласован: {(formats.Count > 0 ? formats[0].FormatName : "unknown")} (8 kHz)");
                _useOpus = false;
            }
        };

        pc.onicecandidate += candidate =>
        {
            try
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.candidate))
                {
                    Diagnostic?.Invoke("локальный сбор ICE-кандидатов завершён");
                    return;
                }

                // Collect for later injection into the offer/answer SDP.
                _gatheredCandidates.Add(candidate.candidate);

                Diagnostic?.Invoke($"локальный кандидат ({CandidateKind(candidate.candidate)}), mid={candidate.sdpMid}");
                LocalIceCandidate?.Invoke(new VoiceIceCandidate(
                    candidate.sdpMid ?? "0", candidate.sdpMLineIndex, candidate.candidate));
            }
            catch (Exception)
            {
                // Listeners must not break the gather loop.
            }
        };
        pc.onicegatheringstatechange += state =>
        {
            Diagnostic?.Invoke($"сбор ICE-кандидатов: {state}");
            // NEKTO PATCH (gathering-complete wait): release callers of
            // CreateOfferAsync / CreateAnswerAsync that are blocking on this so
            // that the returned SDP contains all gathered candidates (relay included).
            if (state == RTCIceGatheringState.complete)
            {
                _gatheringComplete?.TrySetResult(true);
            }
        };
        pc.onconnectionstatechange += state =>
        {
            StateChanged?.Invoke(state.ToString());
            if (state == RTCPeerConnectionState.failed)
            {
                Failed?.Invoke("peer connection failed");
            }
        };
        pc.oniceconnectionstatechange += iceState =>
        {
            IceStateChanged?.Invoke(iceState.ToString());
        };
        pc.OnClosed += () => StateChanged?.Invoke("closed");
        pc.OnRtpClosed += reason => { if (!_closed) Failed?.Invoke($"rtp channel closed: {reason}"); };
        pc.OnRtpPacketReceived += OnRtpPacketReceived;

        lock (_gate)
        {
            _peerConnection = pc;
            _remoteAudioStarted = false;
        }
    }

    public async Task<string> CreateOfferAsync()
    {
        RTCPeerConnection pc = RequirePeer();
        RTCSessionDescriptionInit offer = pc.createOffer(new RTCOfferOptions());
        await pc.setLocalDescription(offer).ConfigureAwait(false);

        // NEKTO PATCH (gather-before-send): wait for ICE gathering to complete so
        // that relay candidates are known before we send the offer.  SIPSorcery uses
        // trickle ICE — localDescription.sdp is NOT updated with gathered candidates,
        // they come only via onicecandidate.  We therefore collect them in
        // _gatheredCandidates and inject them manually into the SDP string below.
        // Without this, the offer is host-only; the peer triggers an ICE restart ~2 s
        // later, burning most of the available 8-second connection window.
        await WaitForGatheringAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);

        return InjectCandidatesIntoSdp(offer.sdp ?? string.Empty, _gatheredCandidates);
    }

    /// <summary>
    /// Inserts the gathered ICE candidates as <c>a=candidate:</c> SDP attribute lines
    /// into the first media section of <paramref name="sdp"/>.  SIPSorcery's trickle-ICE
    /// model fires candidates via <c>onicecandidate</c> without embedding them in
    /// <c>localDescription.sdp</c>, so we inject them here before sending the offer so
    /// the remote peer can use relay→relay on the first attempt.
    /// </summary>
    private static string InjectCandidatesIntoSdp(string sdp, IEnumerable<string> candidateLines)
    {
        string[] toInject = candidateLines
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (toInject.Length == 0)
        {
            return sdp;
        }

        // Detect the line-ending convention used by the SDP (RFC 4566 requires CRLF
        // but implementations sometimes emit LF only).
        string sep = sdp.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        // Find the injection point: right after the first a=mid: line (or the c= line
        // in the first m= section if there is no a=mid:).  Inserting here ensures
        // the candidates are in the correct media section without disturbing the
        // session-level attributes above the first m= block.
        int injectionIndex = -1;
        bool inMediaSection = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("m=", StringComparison.OrdinalIgnoreCase))
            {
                inMediaSection = true;
            }

            if (inMediaSection && line.StartsWith("a=mid:", StringComparison.OrdinalIgnoreCase))
            {
                injectionIndex = i + 1;
                break;
            }
        }

        if (injectionIndex < 0)
        {
            // No a=mid: found — fall back to appending at the end of the first m= section.
            return sdp;
        }

        var result = new List<string>(lines.Length + toInject.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            result.Add(lines[i]);
            if (i == injectionIndex - 1)
            {
                foreach (string c in toInject)
                {
                    // The candidate string from onicecandidate is "candidate:..." (no "a=" prefix).
                    result.Add("a=" + c);
                }
            }
        }

        return string.Join(sep, result);
    }

    public Task SetRemoteDescriptionAsync(string type, string sdp)
    {
        RTCPeerConnection pc = RequirePeer();
        if (!Enum.TryParse(type, ignoreCase: true, out RTCSdpType sdpType))
        {
            throw new InvalidOperationException($"Unknown remote SDP type '{type}'.");
        }

        SetDescriptionResultEnum result = pc.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = sdpType,
            sdp = sdp,
        });
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"Remote description rejected: {result}.");
        }

        return Task.CompletedTask;
    }

    public async Task<string> CreateAnswerAsync()
    {
        RTCPeerConnection pc = RequirePeer();
        RTCSessionDescriptionInit answer = pc.createAnswer(null);
        await pc.setLocalDescription(answer).ConfigureAwait(false);

        // NEKTO PATCH (gather-before-send): same as CreateOfferAsync — wait for
        // relay so the answer also carries relay candidates.  On a repeated
        // peer-connect (session restart), gathering is already complete so this
        // returns instantly with the already-gathered SDP.
        await WaitForGatheringAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        return pc.localDescription?.sdp?.ToString() ?? answer.sdp ?? string.Empty;
    }

    /// <summary>
    /// Blocks until <see cref="_gatheringComplete"/> fires (ICE relay candidate
    /// gathered) or <paramref name="timeout"/> elapses.  Logs both outcomes for
    /// easy diagnosis.
    /// </summary>
    private async Task WaitForGatheringAsync(TimeSpan timeout)
    {
        TaskCompletionSource<bool>? gc = _gatheringComplete;
        if (gc is null || gc.Task.IsCompleted)
        {
            return;
        }

        try
        {
            Diagnostic?.Invoke($"⏳ ожидаем завершения сбора ICE-кандидатов (relay) [{timeout.TotalSeconds:0}s timeout]...");
            await gc.Task.WaitAsync(timeout).ConfigureAwait(false);
            Diagnostic?.Invoke("✓ ICE gathering complete — SDP обновлён, включает relay-кандидаты.");
        }
        catch (TimeoutException)
        {
            Diagnostic?.Invoke("⚠ таймаут ожидания сбора ICE-кандидатов — отправляем SDP без relay (fallback).");
        }
        catch (OperationCanceledException)
        {
            // Swallow; the connection is being torn down.
        }
    }

    public Task AddRemoteIceCandidateAsync(VoiceIceCandidate candidate)
    {
        RTCPeerConnection pc = RequirePeer();
        pc.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate.Candidate,
            sdpMid = candidate.SdpMid,
            sdpMLineIndex = (ushort)Math.Max(0, candidate.SdpMLineIndex),
        });
        return Task.CompletedTask;
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            _muted = muted;
        }
    }

    public void PushMicrophonePcm(short[] samples, int sampleRate)
    {
        bool useOpus = _useOpus;
        int targetRate = useOpus ? 48000 : 8000;
        int frameSamples = useOpus ? 960 : 160;

        // Downsample crudely if the capture rate is higher than the target rate
        if (sampleRate != targetRate && sampleRate % targetRate == 0)
        {
            int factor = sampleRate / targetRate;
            var reduced = new short[samples.Length / factor];
            for (int i = 0; i < reduced.Length; i++)
            {
                reduced[i] = samples[i * factor];
            }
            samples = reduced;
        }

        foreach (short sample in samples)
        {
            _microphone.Enqueue(sample);
        }

        RTCPeerConnection? pc;
        bool muted;
        lock (_gate)
        {
            pc = _peerConnection;
            muted = _muted;
        }

        if (pc is null || pc.connectionState != RTCPeerConnectionState.connected)
        {
            // Keep buffer bounded while waiting for connection
            int maxBuffer = targetRate / 10;
            while (_microphone.Count > maxBuffer)
            {
                _microphone.TryDequeue(out _);
            }
            return;
        }

        while (_microphone.Count >= frameSamples)
        {
            var frame = new short[frameSamples];
            if (!muted)
            {
                for (int i = 0; i < frameSamples; i++)
                {
                    frame[i] = _microphone.TryDequeue(out short sample) ? sample : (short)0;
                }
            }
            else
            {
                for (int i = 0; i < frameSamples; i++)
                {
                    _microphone.TryDequeue(out _);
                }
            }

            try
            {
                byte[] payload;
                if (useOpus)
                {
                    byte[] opusBuffer = new byte[1276];
                    int len = _opusEncoder.Encode(frame, 0, frameSamples, opusBuffer, 0, opusBuffer.Length);
                    payload = new byte[len];
                    Array.Copy(opusBuffer, payload, len);
                }
                else
                {
                    payload = G711.EncodePcmu(frame);
                }
                pc.SendAudio((uint)frameSamples, payload);
            }
            catch (Exception ex)
            {
                if (!_closed)
                {
                    Failed?.Invoke($"send failed: {ex.Message}");
                }
            }
        }
    }

    public void Close()
    {
        _closed = true;
        TurnTcpForwarder? forwarder;
        RTCPeerConnection? pcToClose;
        lock (_gate)
        {
            forwarder = _forwarder;
            _forwarder = null;
            
            pcToClose = _peerConnection;
        }

        forwarder?.Dispose();

        _microphone.Clear();
        
        try
        {
            pcToClose?.Close("client closed");
        }
        catch (Exception)
        {
            // Closing an unconnected session is harmless here.
        }
    }

    public void Dispose()
    {
        Close();
        RTCPeerConnection? pcToDispose;
        lock (_gate)
        {
            pcToDispose = _peerConnection;
            _peerConnection = null;
        }
        
        pcToDispose?.Dispose();
        _dump?.Dispose();
    }

    private RTCPeerConnection RequirePeer() =>
        _peerConnection ?? throw new InvalidOperationException("The audio engine has no active connection.");



    private void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum media, RTPPacket packet)
    {
        if (media != SDPMediaTypesEnum.audio || _closed)
        {
            return;
        }

        if (!_remoteAudioStarted)
        {
            _remoteAudioStarted = true;
            int payloadLength = packet.Payload?.Length ?? 0;
            Diagnostic?.Invoke($"первый RTP-пакет от {remoteEndPoint}: PT={packet.Header.PayloadType}, {payloadLength} байт");
            RemoteAudioStarted?.Invoke();
        }

        if (packet.Payload is not { Length: > 0 } payload || _sink is null)
        {
            return;
        }

        try
        {
            if (_useOpus)
            {
                // WebRTC Opus is typically 20ms frames at 48kHz (960 samples), but can be up to 120ms.
                // 5760 is the maximum possible size (120ms at 48kHz).
                short[] pcm = new short[5760];
                int decoded = _opusDecoder.Decode(payload, 0, payload.Length, pcm, 0, pcm.Length, false);
                if (decoded > 0)
                {
                    if (decoded != pcm.Length)
                    {
                        var exactPcm = new short[decoded];
                        Array.Copy(pcm, exactPcm, decoded);
                        _sink.Write(exactPcm, 48000);
                    }
                    else
                    {
                        _sink.Write(pcm, 48000);
                    }
                }
            }
            else
            {
                _sink.Write(G711.Decode(packet.Header.PayloadType, payload), G711.SampleRate);
            }
        }
        catch (Exception)
        {
            // A failing sink must not kill the receive path.
            // Diagnostic?.Invoke($"Decode error: {ex.Message}");
        }
    }

    /// <summary>Forwards decoded PCM to the real sink while mirroring into the dump.</summary>
    private sealed class TeeSink : IAudioSink
    {
        private readonly IAudioSink? _inner;

        public TeeSink(IAudioSink? inner, WavPcmWriter dump)
        {
            _inner = inner;
            Dump = dump;
        }

        public WavPcmWriter Dump { get; }

        public void Write(short[] pcm, int sampleRate)
        {
            Dump.Write(pcm, sampleRate);
            _inner?.Write(pcm, sampleRate);
        }
    }

    /// <summary>Extracts the candidate type (host/srflx/relay/prflx) from an SDP candidate string.</summary>
    private static string CandidateKind(string candidate)
    {
        Match match = Regex.Match(candidate ?? string.Empty, @"typ\s+(\w+)");
        return match.Success ? match.Groups[1].Value : "?";
    }
}

/// <summary>
/// Microsoft.Extensions.Logging adapter that captures SIPSorcery's internal
/// STUN/TURN/DTLS/ICE transaction logs into a file, so gathering and
/// connectivity-check failures become visible.
/// </summary>
internal sealed class SipsorceryFileLogger : ILogger
{
    private const string LogPath = "/tmp/nekotome-sipsorcery.log";
    private static readonly object FileLock = new();
    private readonly Action<string> _onWarning;

    public SipsorceryFileLogger(Action<string> onWarning)
    {
        _onWarning = onWarning;
        try
        {
            // Fresh log per app launch keeps runs correlatable.
            File.WriteAllText(LogPath, $"=== SIPSorcery log, {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch (Exception)
        {
            // Diagnostics are best effort only.
        }
    }

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    bool ILogger.IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (formatter is null)
        {
            return;
        }

        string line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {formatter(state, exception)}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (FileLock)
        {
            try
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // Diagnostics are best effort only.
            }
        }

        if (logLevel >= LogLevel.Error)
        {
            _onWarning($"⚠ SIPSorcery: {formatter(state, exception)}");
        }
    }
}

/// <summary>Minimal ILoggerFactory handing every category the one file logger.</summary>
internal sealed class SipsorcerySingleLoggerFactory : ILoggerFactory
{
    private readonly ILogger _logger;

    public SipsorcerySingleLoggerFactory(ILogger logger)
    {
        _logger = logger;
    }

    ILogger ILoggerFactory.CreateLogger(string categoryName) => _logger;

    void ILoggerFactory.AddProvider(ILoggerProvider provider)
    {
        // Single fixed destination; additional providers are ignored.
    }

    void IDisposable.Dispose()
    {
    }
}
