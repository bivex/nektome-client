using System.Text.Json;
using System.Text.RegularExpressions;
using NektoMe.Application.Abstractions;
using NektoMe.Application.Events;
using NektoMe.Application.Protocol;

namespace NektoMe.Application.Services;

/// <summary>
/// Voice chat roulette orchestrator. Mirrors the reference voice app's
/// presenter: register on connect, scan, react to <c>peer-connect</c> by
/// building one WebRTC connection through <see cref="IAudioEngine"/>, bridge
/// SDP/ICE, and surface everything as <see cref="VoiceChatEvent"/>s.
/// </summary>
public sealed class VoiceChatSession : IDisposable
{
    private readonly IVoiceTransport _transport;
    private readonly IAudioServerEndpointResolver _endpoints;
    private readonly IVoiceIdentityProvider _identity;
    private readonly Func<IAudioSink?, IAudioEngine> _engineFactory;
    private readonly IMicrophoneCapture? _microphone;
    private readonly IPingProber? _pingProber;
    private readonly VoiceOptions _options;

    private readonly Queue<VoiceIceCandidate> _pendingRemoteCandidates = new();

    // Remote candidates seen for the current peer id, kept across engine rebuilds.
    // When the server re-matches the same person (double peer-connect), the fresh
    // engine replays them so TURN permissions cover every peer address — including
    // host IPs that the new SDP omits. Without this, coturn silently drops the
    // peer's checks originating from unpermitted IPs and ICE never completes.
    private readonly Dictionary<string, List<VoiceIceCandidate>> _remoteCandidateHistory = new();

    private VoiceSearchCriteria _criteria;
    private string? _captchaToken;
    private string? _captchaTokenType;
    private string? _connectionId;      // last id the server gave us on register
    private string? _resumePeerId;      // set on soft-disconnect, consumed by next register
    private string? _peerConnectionId;  // active chat id from peer-connect
    private IAudioEngine? _engine;
    private IAudioSink? _sink;
    private bool _searching;
    private bool _muted;
    private bool _mediaEstablished;
    private bool _remoteDescriptionSet;
    private bool _disposed;
    private bool _engineBroken;
    private CancellationTokenSource? _peerWatchdogCts;
    private readonly List<string> _localCandidateKinds = new();
    private readonly List<string> _remoteCandidateKinds = new();
    private CancellationTokenSource? _iceConnectTimeoutCts;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);

    public VoiceChatSession(
        IVoiceTransport transport,
        IAudioServerEndpointResolver endpoints,
        IVoiceIdentityProvider identity,
        Func<IAudioSink?, IAudioEngine> engineFactory,
        VoiceOptions options,
        IMicrophoneCapture? microphone = null,
        IPingProber? pingProber = null)
    {
        _transport = transport;
        _endpoints = endpoints;
        _identity = identity;
        _engineFactory = engineFactory;
        _options = options;
        _microphone = microphone;
        _pingProber = pingProber;
        _criteria = options.Search;

        if (_microphone is not null)
        {
            _microphone.PcmCaptured += OnMicrophonePcm;
        }

        _transport.Connected += OnTransportConnected;
        _transport.Disconnected += OnTransportDisconnected;
        _transport.Reconnecting += () => Events.Publish(new VoiceReconnectingStarted());
        _transport.TransportError += message => Events.Publish(new VoiceTransportFailed(message));
        _transport.EventReceived += e => _ = DispatchSafeAsync(e);
    }

    /// <summary>Voice events for UI/diagnostics consumers.</summary>
    public VoiceEventStream Events { get; } = new();

    public bool IsConnected => _transport.IsConnected;

    /// <summary>True while a peer chat exists (from <c>peer-connect</c> to hangup/disconnect).</summary>
    public bool InChat => _peerConnectionId is not null;

    /// <summary>True once remote audio started flowing.</summary>
    public bool MediaEstablished => _mediaEstablished;

    /// <summary>Injects the remote-audio sink; pass null for receive-less operation. Must precede <see cref="ConnectAsync"/>.</summary>
    public void SetAudioSink(IAudioSink? sink) => _sink = sink;

    /// <summary>Underlying microphone capture instance, if configured.</summary>
    public IMicrophoneCapture? Microphone => _microphone;

    /// <summary>Volume multiplier (1.0 = normal, 2.0 = +6dB).</summary>
    public float MicGain { get; set; } = 2.0f;

    // -- use cases -------------------------------------------------------------

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AudioServerEndpoint endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        await _transport.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartSearchAsync(VoiceSearchCriteria? criteria = null, string? captchaToken = null, string? captchaTokenType = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (criteria is not null)
        {
            _criteria = criteria;
        }

        _captchaToken = captchaToken;
        _captchaTokenType = captchaTokenType;
        _searching = true;
        Events.Publish(new VoiceSearchStateChanged(Searching: true));
        await SendAsync(VoiceCodec.ScanForPeer(_criteria, _captchaToken, _captchaTokenType), cancellationToken).ConfigureAwait(false);
    }

    public async Task StopSearchAsync(CancellationToken cancellationToken = default)
    {
        _searching = false;
        _captchaToken = null;
        _captchaTokenType = null;
        Events.Publish(new VoiceSearchStateChanged(Searching: false));
        await SendAsync(VoiceCodec.StopScan(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Solves a <see cref="VoiceCaptchaRequested"/> by re-scanning with the token.</summary>
    public async Task SubmitCaptchaAsync(string token, string? tokenType = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _captchaToken = token;
        _captchaTokenType = tokenType;
        _searching = true;

        // Mirror official Android APK (SearchProcessFragment.onViewClicked -> SearchProcessPresenter.runSearch):
        // Directly send scan-for-peer with the captcha solution token without cancelling via stop-scan.
        await SendAsync(VoiceCodec.ScanForPeer(_criteria, _captchaToken, _captchaTokenType), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        _muted = muted;
        _engine?.SetMuted(muted);
        if (_peerConnectionId is { } id)
        {
            await SendAsync(VoiceCodec.PeerMute(id, muted), cancellationToken).ConfigureAwait(false);
        }
    }

    public Task HangupAsync(CancellationToken cancellationToken = default) =>
        EndChatAsync(sendDisconnect: true, soft: false, cancellationToken);

    /// <summary>Reports connection trouble to the server (kept alive request).</summary>
    public async Task TroubleAsync(CancellationToken cancellationToken = default)
    {
        if (_peerConnectionId is { } id)
        {
            await SendAsync(VoiceCodec.PeerTrouble(id), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Refreshes the online/waiting counters (also requested after register).</summary>
    public async Task RefreshUsersCountAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(VoiceCodec.UsersCountRequest(), cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_searching)
        {
            _searching = false;
            Events.Publish(new VoiceSearchStateChanged(Searching: false));
        }

        CloseEngine();
        await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Active identity provider.</summary>
    public IVoiceIdentityProvider IdentityProvider => _identity;

    /// <summary>Voice session options.</summary>
    public VoiceOptions Options => _options;

    /// <summary>Resets the voice identity (userId) and clears connection state.</summary>
    public string ResetIdentity(string? prefix = null)
    {
        _connectionId = null;
        _resumePeerId = null;
        return _identity.ResetIdentity(prefix);
    }

    /// <summary>Explicitly sets the voice identity (userId) and clears connection state.</summary>
    public void SetIdentity(string userId)
    {
        _connectionId = null;
        _resumePeerId = null;
        _identity.SetUserId(userId);
    }

    // -- transport reactions ----------------------------------------------------

    private void OnTransportConnected()
    {
        Events.Publish(new VoiceConnectionChanged(Connected: true, Reason: null));
        _ = SendRegisterAsync();
    }

    private void OnTransportDisconnected(string? reason)
    {
        CloseEngine();
        // Searching intent survives transport drops; re-registration re-sends the
        // scan because the server forgot it. Only user actions clear it.
        _peerConnectionId = null;
        _mediaEstablished = false;
        Events.Publish(new VoiceConnectionChanged(Connected: false, Reason: reason));
    }

    private async Task SendRegisterAsync()
    {
        try
        {
            if (_options.ProtocolMode == VoiceProtocolMode.Web)
            {
                string token = _identity.GetWebToken();
                string locale = _options.Locale ?? "ru";
                string timeZone = _options.TimeZone ?? TimeZoneInfo.Local.Id;
                await SendAsync(VoiceCodec.RegisterWeb(token, locale, timeZone)).ConfigureAwait(false);
            }
            else
            {
                // The server resumes a just-ended chat when the next register repeats
                // its connection id as peerSuccess; the field is sent once only.
                // Like the reference's connectionIdLast, the id persisted by a previous
                // run is re-sent on the first register of a fresh session.
                string userId = _identity.GetUserId();
                if (string.IsNullOrWhiteSpace(userId) ||
                    (userId.Length == 32 && !userId.Contains('-') && Guid.TryParseExact(userId, "N", out _)))
                {
                    userId = _identity.ResetIdentity();
                }

                _connectionId ??= _identity.GetLastConnectionId();
                await SendAsync(VoiceCodec.Register(userId, _connectionId, _resumePeerId, _options)).ConfigureAwait(false);
                _resumePeerId = null;
            }
        }
        catch (ObjectDisposedException)
        {
            // Benign teardown if disconnect occurred while registering
        }
        catch (OperationCanceledException)
        {
            // Benign teardown
        }
        catch (Exception ex)
        {
            if (_transport.IsConnected)
            {
                Events.Publish(new VoiceTransportFailed($"register failed: {ex.Message}"));
            }
        }
    }

    // -- server event dispatch ---------------------------------------------------

    private async Task DispatchSafeAsync(VoiceWireEvent e)
    {
        await _dispatchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DispatchAsync(e).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Events.Publish(new VoiceTransportFailed($"voice event '{e.Type}' failed: {ex.Message}"));
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    private async Task DispatchAsync(VoiceWireEvent e)
    {
        switch (e.Type)
        {
            case VoiceWireNames.Registered:
                await HandleRegisteredAsync(e).ConfigureAwait(false);
                break;

            case VoiceWireNames.SearchSuccess:
                _searching = true;
                Events.Publish(new VoiceSearchStateChanged(Searching: true));
                break;

            case VoiceWireNames.SearchOut:
            case VoiceWireNames.SearchStop:
                _searching = false;
                _captchaToken = null;
                _captchaTokenType = null;
                Events.Publish(new VoiceSearchStateChanged(Searching: false));
                break;

            case VoiceWireNames.PeerConnect:
                await HandlePeerConnectAsync(e).ConfigureAwait(false);
                break;

            case VoiceWireNames.PeerConnection:
                Events.Publish(new VoicePeerConnected(
                    VoiceCodec.Parse<VoiceConnectionData>(e).ConnectionId ?? string.Empty));
                break;

            case VoiceWireNames.Offer:
            case VoiceWireNames.Answer:
                await HandleSdpAsync(e).ConfigureAwait(false);
                break;

            case VoiceWireNames.IceCandidate:
                await HandleIceCandidateAsync(e).ConfigureAwait(false);
                break;

            case VoiceWireNames.PeerDisconnect:
                EndChatRemote(VoiceCodec.Parse<VoiceConnectionData>(e).ConnectionId, soft: false);
                break;

            case VoiceWireNames.PeerSoftDisconnect:
                EndChatRemote(VoiceCodec.Parse<VoiceConnectionData>(e).ConnectionId, soft: true);
                break;

            case VoiceWireNames.PeerMute:
                Events.Publish(new VoicePeerMuteChanged(
                    VoiceCodec.Parse<VoicePeerMuteData>(e).Muted ?? false));
                break;

            case VoiceWireNames.UsersCount:
            {
                VoiceUsersCountData data = VoiceCodec.Parse<VoiceUsersCountData>(e);
                Events.Publish(new VoiceUsersCount(
                    data.UsersCount ?? 0, data.WaitingUsersCount ?? 0, data.TalkingUsersCount ?? 0));
                break;
            }

            case VoiceWireNames.CaptchaRequest:
            {
                VoiceCaptchaRequestData data = VoiceCodec.Parse<VoiceCaptchaRequestData>(e);
                _searching = false;
                Events.Publish(new VoiceSearchStateChanged(Searching: false));
                Events.Publish(new VoiceCaptchaRequested(
                    data.CaptchaType, data.Url, data.LeftChats ?? 0, data.NeedChats ?? 0));
                break;
            }

            case VoiceWireNames.Ban:
                _searching = false;
                _connectionId = null;
                _identity.ResetIdentity();
                Events.Publish(new VoiceSearchStateChanged(Searching: false));
                JsonElement banInfo = e.Root.TryGetProperty("banInfo", out JsonElement info) && info.ValueKind == JsonValueKind.Object
                    ? info
                    : e.Root;
                Events.Publish(new VoiceBanned(
                    BanEnum: banInfo.TryGetProperty("banEnum", out JsonElement kind) && kind.ValueKind == JsonValueKind.String
                        ? kind.GetString()
                        : null,
                    Permanently: banInfo.TryGetProperty("permanently", out JsonElement perm) && perm.ValueKind == JsonValueKind.True,
                    Text: banInfo.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String
                        ? text.GetString()
                        : null,
                    Detail: e.Root.GetRawText()));
                break;

            case VoiceWireNames.Error:
                HandleError(e);
                break;

            case VoiceWireNames.PurchaseChanged:
                Events.Publish(new VoicePurchaseChanged(
                    e.Root.TryGetProperty("paidType", out JsonElement paid) && paid.ValueKind == JsonValueKind.Number
                        ? paid.GetInt32()
                        : null));
                break;

            case VoiceWireNames.PingServerRequest:
                if (e.Root.TryGetProperty("echo", out _))
                {
                    Events.Publish(new VoiceDiagnosticMessage("🛡 Получен Web-пинг сервера (echo), отправлен ответ ping-server-response"));
                    await SendAsync(WebAntibotHelper.BuildPingServerResponse(e.Root)).ConfigureAwait(false);
                }
                else
                {
                    await HandlePingServerRequestAsync(e).ConfigureAwait(false);
                }
                break;

            case "challenge" or "challenge-request" or "challenge-proof":
                Events.Publish(new VoiceDiagnosticMessage($"🛡 Получен Web antibot challenge '{e.Type}', отправка challenge-proof..."));
                await SendAsync(WebAntibotHelper.BuildChallengeProof(e.Root)).ConfigureAwait(false);
                break;

            case "challenge-sync":
                Events.Publish(new VoiceDiagnosticMessage("🛡 Получен Web challenge-sync, отправка challenge-ack..."));
                await SendAsync(WebAntibotHelper.BuildChallengeAck(e.Root)).ConfigureAwait(false);
                break;

            case "challenge-trace":
                Events.Publish(new VoiceDiagnosticMessage("🛡 Получен Web challenge-trace, отправка challenge-trace..."));
                await SendAsync(WebAntibotHelper.BuildChallengeTrace(e.Root)).ConfigureAwait(false);
                break;

            case "challenge-ack":
                Events.Publish(new VoiceDiagnosticMessage("🛡 Web challenge подтверждён сервером (challenge-ack) ✓"));
                break;

            default:
                Events.Publish(new VoiceRawEvent(e.Type, e.Root.GetRawText()));
                break;
        }
    }

    private async Task HandlePingServerRequestAsync(VoiceWireEvent e)
    {
        VoicePingServerRequestData data = VoiceCodec.Parse<VoicePingServerRequestData>(e);
        List<string> servers = data.List ?? [];
        int attempts = Math.Max(1, data.Attempts ?? 1);

        Events.Publish(new VoiceDiagnosticMessage($"⚡ Получен запрос пинга ({servers.Count} серверов, попыток: {attempts}), измерение задержки..."));

        List<VoicePingResultData> results = new(servers.Count);
        if (_pingProber is { } prober)
        {
            foreach (VoicePingSample sample in await prober
                .ProbeAsync(servers, attempts).ConfigureAwait(false))
            {
                results.Add(new VoicePingResultData(sample.Server, sample.RttMs, sample.Fails));
            }
        }
        else
        {
            // No probe adapter wired: report every host as unreachable, like the
            // reference app does when its ICMP probe fails outright.
            foreach (string server in servers)
            {
                results.Add(new VoicePingResultData(server, -1, attempts));
            }
        }

        await SendAsync(VoiceCodec.LogPingResults(results)).ConfigureAwait(false);
        Events.Publish(new VoiceDiagnosticMessage($"✓ Результаты пинга ({results.Count} серверов) отправлены на сервер"));
    }

    private async Task HandleRegisteredAsync(VoiceWireEvent e)
    {
        VoiceRegisteredData data = VoiceCodec.Parse<VoiceRegisteredData>(e);
        bool success = data.Success ?? false;
        if (!string.IsNullOrEmpty(data.ConnectionId))
        {
            _connectionId = data.ConnectionId;
            _identity.SaveLastConnectionId(data.ConnectionId);
        }

        Events.Publish(new VoiceRegistered(success, data.ConnectionId, data.ErrorCode, data.RecaptchaSiteKey));

        if (success)
        {
            if (_options.ProtocolMode == VoiceProtocolMode.Web)
            {
                if (e.Root.TryGetProperty("internal_id", out JsonElement internalIdProp) &&
                    internalIdProp.TryGetInt64(out long internalId))
                {
                    string token = _identity.GetWebToken();
                    string timeZone = _options.TimeZone ?? TimeZoneInfo.Local.Id;
                    await SendAsync(WebAntibotHelper.BuildWebAgent(token, internalId)).ConfigureAwait(false);
                    await SendAsync(WebAntibotHelper.BuildWebFpt(token, internalId, timeZone: timeZone)).ConfigureAwait(false);
                }
            }

            if (_searching)
            {
                // Reconnect mid-search: the server forgot our scan.
                await SendAsync(VoiceCodec.ScanForPeer(_criteria, _captchaToken, _captchaTokenType)).ConfigureAwait(false);
            }
        }
    }

    private async Task HandlePeerConnectAsync(VoiceWireEvent e)
    {
        VoicePeerConnectData data = VoiceCodec.Parse<VoicePeerConnectData>(e);
        string connectionId = data.ConnectionId
            ?? throw new JsonException("peer-connect without connectionId.");

        if (_engine is not null && _peerConnectionId == connectionId && !_engineBroken)
        {
            // Server re-issued peer-connect for the person we are already
            // negotiating with. Tearing the engine down here has been observed to
            // kill a session whose ICE pair had just succeeded; keep it running.
            Events.Publish(new VoiceDiagnosticMessage(
                $"🔁 Повторный peer-connect для {connectionId} — сохраняю активную сессию, движок не пересоздаю"));
            return;
        }

        _peerConnectionId = connectionId;
        _engineBroken = false;
        _searching = false;
        _mediaEstablished = false;
        _peerConnectionSignalSent = false;
        _muted = false;

        // Candidate history is only meaningful per peer; drop everyone else's.
        foreach (string staleId in _remoteCandidateHistory.Keys.Where(k => k != connectionId).ToList())
        {
            _remoteCandidateHistory.Remove(staleId);
        }
        bool initiator = data.Initiator ?? false;
        bool relay = data.Relay ?? false;
        Events.Publish(new VoicePeerFound(connectionId, initiator, relay));

        int turnCount = data.TurnParams?.Count ?? 0;
        string stunInfo = string.IsNullOrEmpty(data.StunUrl) ? "none" : data.StunUrl;
        Events.Publish(new VoiceDiagnosticMessage(
            $"🎯 Найден собеседник! ID: {connectionId}, Роль: {(initiator ? "Инициатор (Caller)" : "Принимающий (Answerer)")}, Relay: {relay}, TURN: {turnCount} серв., STUN: {stunInfo}"));

        CloseEngine();
        IAudioEngine engine = _engineFactory(_sink);
        _engine = engine;
        HookEngine(engine);
        StartWatchdog(connectionId);

        var servers = new List<VoiceIceServer>();
        if (data.TurnParams is not null)
        {
            servers.AddRange(data.TurnParams
                .Where(p => !string.IsNullOrEmpty(p.Url))
                .Select(p => new VoiceIceServer(p.Url!, p.Username, p.Credential)));
        }

        if (!string.IsNullOrEmpty(data.StunUrl))
        {
            servers.Add(new VoiceIceServer(data.StunUrl, null, null));
        }
        else if (servers.Count == 0)
        {
            servers.Add(new VoiceIceServer("stun:stun.l.google.com:19302", null, null));
        }

        await engine.CreateConnectionAsync(servers, relay).ConfigureAwait(false);
        _remoteDescriptionSet = false;
        _pendingRemoteCandidates.Clear();
        _localCandidateKinds.Clear();
        _remoteCandidateKinds.Clear();

        // Replay the peer's candidates from previous negotiations of the same id:
        // each one makes SIPSorcery install a TURN permission for its IP, so the
        // peer's checks sent from addresses the new SDP omits still get delivered.
        if (_remoteCandidateHistory.Remove(connectionId, out List<VoiceIceCandidate>? history))
        {
            int replayed = 0;
            foreach (VoiceIceCandidate previous in history)
            {
                if (_pendingRemoteCandidates.Count < 32)
                {
                    _pendingRemoteCandidates.Enqueue(previous);
                    replayed++;
                }
            }

            if (replayed > 0)
            {
                Events.Publish(new VoiceDiagnosticMessage(
                    $"♻ Накоплено {replayed} прошлых ICE-кандидатов собеседника — будут применены после SDP (permissions для всех его IP)"));
            }
        }
        Events.Publish(new VoiceDiagnosticMessage($"✓ WebRTC движок запущен ({servers.Count} ICE серверов)"));

        if (initiator)
        {
            // Reference APK (AudioChatPresenter.onPeerConnect): initiator only
            // creates and sends the offer here. peer-mute is deferred until the
            // media is up — setAndSendMute fires together with peer-connection:true.
            string offer = await engine.CreateOfferAsync().ConfigureAwait(false);
            Events.Publish(new VoiceDiagnosticMessage($"📤 Мы инициатор — сформирован и отправлен SDP offer ({offer.Length} симв.)"));
            await SendAsync(VoiceCodec.Offer(connectionId, "offer", offer)).ConfigureAwait(false);
        }
        else
        {
            Events.Publish(new VoiceDiagnosticMessage("⏳ Мы принимающая сторона — ожидаем SDP offer от собеседника..."));
        }
    }

    private void HookEngine(IAudioEngine engine)
    {
        engine.LocalIceCandidate += OnEngineLocalIceCandidate;
        engine.RemoteAudioStarted += OnRemoteAudioStarted;
        engine.StateChanged += OnEngineStateChanged;
        engine.IceStateChanged += OnEngineIceStateChanged;
        engine.Failed += OnEngineFailed;
        engine.Diagnostic += OnEngineDiagnostic;
    }

    private void UnhookEngine(IAudioEngine engine)
    {
        engine.LocalIceCandidate -= OnEngineLocalIceCandidate;
        engine.RemoteAudioStarted -= OnRemoteAudioStarted;
        engine.StateChanged -= OnEngineStateChanged;
        engine.IceStateChanged -= OnEngineIceStateChanged;
        engine.Failed -= OnEngineFailed;
        engine.Diagnostic -= OnEngineDiagnostic;
    }

    private void OnEngineDiagnostic(string message) =>
        Events.Publish(new VoiceDiagnosticMessage($"🔬 {message}"));

    private void OnEngineLocalIceCandidate(VoiceIceCandidate candidate) =>
        _ = SendLocalCandidateAsync(candidate);

    private void OnEngineStateChanged(string state)
    {
        Events.Publish(new VoiceMediaStateChanged(state));
        if (state.Equals("connected", StringComparison.OrdinalIgnoreCase))
        {
            CancelWatchdog();
            OnMediaConnected();
        }
    }

    private void OnEngineIceStateChanged(string iceState)
    {
        Events.Publish(new VoiceMediaStateChanged($"ice:{iceState}"));
        if (iceState.Equals("connected", StringComparison.OrdinalIgnoreCase)
            || iceState.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            _ = SignalPeerMediaUpAsync();
        }
        else if (iceState.Equals("disconnected", StringComparison.OrdinalIgnoreCase)
            || iceState.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || iceState.Equals("closed", StringComparison.OrdinalIgnoreCase))
        {
            _engineBroken = true;
            _ = SignalPeerMediaDownAsync();
        }
    }

    private void OnEngineFailed(string message)
    {
        _engineBroken = true;
        Events.Publish(new VoiceTransportFailed($"media: {message}"));
    }

    private void StartWatchdog(string connectionId)
    {
        CancelWatchdog();
        _peerWatchdogCts = new CancellationTokenSource();
        _ = RunPeerWatchdogAsync(connectionId, _peerWatchdogCts.Token);
    }

    private void CancelWatchdog()
    {
        if (_peerWatchdogCts is { } cts)
        {
            _peerWatchdogCts = null;
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch { }
        }
    }

    /// <summary>
    /// 12 s after the remote description lands, if media never came up, publish
    /// a candidate-statistics summary — the fastest way to see that e.g. only a
    /// host candidate was gathered while the remote offered relay ones.
    /// </summary>
    private void StartIceConnectTimeout()
    {
        CancelIceConnectTimeout();
        _iceConnectTimeoutCts = new CancellationTokenSource();
        CancellationToken ct = _iceConnectTimeoutCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || _peerConnectionSignalSent || _mediaEstablished)
                {
                    return;
                }

                string localKinds;
                lock (_localCandidateKinds)
                {
                    localKinds = _localCandidateKinds.Count == 0 ? "нет" : string.Join(", ", _localCandidateKinds);
                }

                string remoteKinds;
                lock (_remoteCandidateKinds)
                {
                    remoteKinds = _remoteCandidateKinds.Count == 0 ? "нет" : string.Join(", ", _remoteCandidateKinds);
                }

                Events.Publish(new VoiceDiagnosticMessage(
                    $"⏱ ICE не подключился за 12 сек: локальные кандидаты [{localKinds}], удалённые [{remoteKinds}]. Подробности в /tmp/nekotome-sipsorcery.log"));
            }
            catch (OperationCanceledException)
            {
                // Media came up before the deadline — expected path.
            }
            catch (Exception)
            {
                // Diagnostics must never break the session.
            }
        }, ct);
    }

    private void CancelIceConnectTimeout()
    {
        if (_iceConnectTimeoutCts is { } cts)
        {
            _iceConnectTimeoutCts = null;
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch { }
        }
    }

    private async Task RunPeerWatchdogAsync(string connectionId, CancellationToken ct)
    {
        try
        {
            int timeoutSeconds = _options.PeerConnectTimeoutSeconds;
            if (timeoutSeconds <= 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested && _peerConnectionId == connectionId && !_mediaEstablished)
            {
                Events.Publish(new VoiceDiagnosticMessage(
                    $"⏱ Таймаут ожидания соединения ({timeoutSeconds} сек). Собеседник {connectionId} завис или не отвечает по WebRTC. Переподключение..."));
                await RecoverStalledPeerAsync(connectionId).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when media succeeds or chat closes
        }
        catch (Exception ex)
        {
            Events.Publish(new VoiceTransportFailed($"watchdog error: {ex.Message}"));
        }
    }

    private async Task RecoverStalledPeerAsync(string connectionId)
    {
        try
        {
            await SendAsync(VoiceCodec.PeerDisconnect(connectionId)).ConfigureAwait(false);
        }
        catch { }

        CloseEngine();
        _peerConnectionId = null;
        _mediaEstablished = false;
        _muted = false;
        Events.Publish(new VoicePeerGone(connectionId, Soft: false));
        Events.Publish(new VoiceDiagnosticMessage("🔄 Перезапуск поиска нового собеседника..."));
        await StartSearchAsync(_criteria, _captchaToken, _captchaTokenType).ConfigureAwait(false);
    }

    private bool _peerConnectionSignalSent;

    /// <summary>
    /// Media is up (ICE connected/completed or remote audio arrived). Sends the
    /// reference triple exactly once, in the APK's order: stream-received →
    /// peer-connection:true → peer-mute (AudioChatPresenter.onConnectionChange,
    /// IceConnectionState CONNECTED/COMPLETED branch).
    /// </summary>
    private async Task SignalPeerMediaUpAsync()
    {
        if (_peerConnectionSignalSent) return;
        _peerConnectionSignalSent = true;
        CancelIceConnectTimeout();
        _microphone?.Start();
        if (_peerConnectionId is { } id)
        {
            Events.Publish(new VoiceDiagnosticMessage("🤝 Медиа готово! Отправка stream-received, peer-connection:true и peer-mute"));
            await SendAsync(VoiceCodec.StreamReceived(id)).ConfigureAwait(false);
            await SendAsync(VoiceCodec.PeerConnection(true, id)).ConfigureAwait(false);
            await SendAsync(VoiceCodec.PeerMute(id, _muted)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ICE dropped/failed/closed after a session that had signaled media up.
    /// Mirrors the APK: report peer-connection:false once and re-arm the guard.
    /// </summary>
    private async Task SignalPeerMediaDownAsync()
    {
        if (!_peerConnectionSignalSent) return;
        _peerConnectionSignalSent = false;
        if (_peerConnectionId is { } id)
        {
            Events.Publish(new VoiceDiagnosticMessage("📴 ICE потерян — отправка peer-connection:false"));
            await SendAsync(VoiceCodec.PeerConnection(false, id)).ConfigureAwait(false);
        }
    }

    private void OnMediaConnected()
    {
        // Full DTLS+ICE connection ready — start mic if not yet started by SignalPeerMediaUpAsync.
        _microphone?.Start();
    }

    private async Task SendLocalCandidateAsync(VoiceIceCandidate candidate)
    {
        if (_peerConnectionId is not { } id)
        {
            return;
        }

        string kind = CandidateKind(candidate.Candidate);
        lock (_localCandidateKinds)
        {
            _localCandidateKinds.Add(kind);
        }

        try
        {
            Events.Publish(new VoiceDiagnosticMessage($"🧊 Отправлен локальный ICE-кандидат ({kind}): mid={candidate.SdpMid}, idx={candidate.SdpMLineIndex}"));
            await SendAsync(VoiceCodec.IceCandidate(id, candidate)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Events.Publish(new VoiceTransportFailed($"ice send failed: {ex.Message}"));
        }
    }

    private void OnRemoteAudioStarted()
    {
        _mediaEstablished = true;
        // The wire triple (stream-received / peer-connection:true / peer-mute)
        // is owned by SignalPeerMediaUpAsync; whichever of ICE-connected or
        // remote-audio fires first sends it, exactly once, and starts the mic.
        _ = SignalPeerMediaUpAsync();

        Events.Publish(new VoiceMediaEstablished(_peerConnectionId ?? string.Empty));
    }

    private readonly AudioDynamicsProcessor _micDsp = new AudioDynamicsProcessor() { IsMicrophone = true };
    public bool MicDspEnabled { get => _micDsp.IsEnabled; set => _micDsp.IsEnabled = value; }
    public bool EchoCancellationEnabled { get => _micDsp.EchoCancellationEnabled; set => _micDsp.EchoCancellationEnabled = value; }

    // Mic level logging: publishes one throttled diagnostic per interval so the
    // log shows what the peer actually receives (post-DSP, post-gain) versus the
    // raw capture. This is how a dead-silent "they can't hear me" is diagnosed.
    private DateTime _lastMicLevelLogUtc = DateTime.MinValue;
    private static readonly TimeSpan MicLevelLogInterval = TimeSpan.FromSeconds(2);

    private static (double Rms, double Peak) MeasureLevels(short[] pcm)
    {
        double sumSquares = 0;
        double peak = 0;
        for (int i = 0; i < pcm.Length; i++)
        {
            double v = pcm[i];
            sumSquares += v * v;
            double abs = Math.Abs(v);
            if (abs > peak) peak = abs;
        }
        double rms = pcm.Length == 0 ? 0 : Math.Sqrt(sumSquares / pcm.Length);
        return (rms, peak);
    }

    private static string FormatDbfs(double rms, double peak)
    {
        // Below -90 dBFS everything is effectively digital silence.
        string Db(double value) => value < -90 ? "-∞" : value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        double rmsDb = rms <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(rms / 32767.0);
        double peakDb = peak <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(peak / 32767.0);
        return $"{Db(rmsDb)} dBFS (пик {Db(peakDb)})";
    }

    private void OnMicrophonePcm(short[] pcm, int sampleRate)
    {
        // Drop frames while muted or not in chat
        if (_muted || _peerConnectionId is null)
        {
            return;
        }

        _micDsp.SetSampleRate(sampleRate);
        (double rawRms, double rawPeak) = MeasureLevels(pcm);
        _micDsp.Process(pcm);

        if (Math.Abs(MicGain - 1.0f) > 0.01f)
        {
            var gained = new short[pcm.Length];
            for (int i = 0; i < pcm.Length; i++)
            {
                gained[i] = (short)Math.Clamp((int)Math.Round(pcm[i] * MicGain), short.MinValue, short.MaxValue);
            }
            pcm = gained;
        }

        DateTime now = DateTime.UtcNow;
        if (now - _lastMicLevelLogUtc >= MicLevelLogInterval)
        {
            _lastMicLevelLogUtc = now;
            (double outRms, double outPeak) = MeasureLevels(pcm);
            Events.Publish(new VoiceDiagnosticMessage(
                $"🔬 Микрофон: вход {FormatDbfs(rawRms, rawPeak)} → в эфир {FormatDbfs(outRms, outPeak)}; усиление {MicGain * 100:F0}%"));
        }

        _engine?.PushMicrophonePcm(pcm, sampleRate);
    }

    private async Task HandleSdpAsync(VoiceWireEvent e)
    {
        (string type, string sdp, string connectionId) = VoiceCodec.ExtractSdp(e);
        if (!string.IsNullOrEmpty(connectionId) && _peerConnectionId != null && connectionId != _peerConnectionId)
        {
            Events.Publish(new VoiceDiagnosticMessage($"⚠ Игнорируем SDP ({type}) от прошлого собеседника ({connectionId})"));
            return;
        }

        if (string.IsNullOrEmpty(sdp))
        {
            throw new JsonException($"voice event '{e.Type}' carries no SDP.");
        }

        Events.Publish(new VoiceDiagnosticMessage($"📥 Получен SDP {type} ({sdp.Length} симв.)"));
        IAudioEngine engine = RequireEngine();

        await engine.SetRemoteDescriptionAsync(type, sdp).ConfigureAwait(false);
        _remoteDescriptionSet = true;
        Events.Publish(new VoiceDiagnosticMessage($"✓ Remote description ({type}) установлен"));
        StartIceConnectTimeout();

        if (e.Type == VoiceWireNames.Offer)
        {
            string answer = await engine.CreateAnswerAsync().ConfigureAwait(false);
            string id = _peerConnectionId ?? (string.IsNullOrEmpty(connectionId) ? null : connectionId)
                ?? throw new JsonException("answer without connectionId.");
            Events.Publish(new VoiceDiagnosticMessage($"📤 Сформирован и отправлен SDP answer ({answer.Length} симв.)"));
            await SendAsync(VoiceCodec.Answer(id, "answer", answer)).ConfigureAwait(false);
        }

        await FlushPendingCandidatesAsync().ConfigureAwait(false);
    }

    private async Task HandleIceCandidateAsync(VoiceWireEvent e)
    {
        (VoiceIceCandidate? candidate, string connectionId) = VoiceCodec.ExtractCandidate(e);
        if (!string.IsNullOrEmpty(connectionId) && _peerConnectionId != null && connectionId != _peerConnectionId)
        {
            return; // Ignore stale candidates from a previous peer
        }

        if (candidate is null)
        {
            return;
        }

        string kind = CandidateKind(candidate.Candidate);
        lock (_remoteCandidateKinds)
        {
            _remoteCandidateKinds.Add(kind);
        }

        string historyKey = string.IsNullOrEmpty(connectionId) ? (_peerConnectionId ?? string.Empty) : connectionId;
        if (historyKey.Length > 0)
        {
            if (!_remoteCandidateHistory.TryGetValue(historyKey, out List<VoiceIceCandidate>? list))
            {
                list = new List<VoiceIceCandidate>();
                _remoteCandidateHistory[historyKey] = list;
            }

            if (list.Contains(candidate))
            {
                return;
            }

            list.Add(candidate);
            if (list.Count > 32)
            {
                list.RemoveAt(0);
            }
        }

        Events.Publish(new VoiceDiagnosticMessage($"🧊 Получен удалённый ICE-кандидат ({kind}): mid={candidate.SdpMid}, idx={candidate.SdpMLineIndex}"));
        if (!_remoteDescriptionSet)
        {
            // libwebrtc peers may trickle before our answer lands; queue them.
            _pendingRemoteCandidates.Enqueue(candidate);
            return;
        }

        IAudioEngine? engine = _engine;
        if (engine is not null)
        {
            try
            {
                await engine.AddRemoteIceCandidateAsync(candidate).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Events.Publish(new VoiceDiagnosticMessage($"⚠ Ошибка добавления ICE-кандидата: {ex.Message}"));
            }
        }
    }

    private async Task FlushPendingCandidatesAsync()
    {
        IAudioEngine? engine = _engine;
        if (engine is null)
        {
            _pendingRemoteCandidates.Clear();
            return;
        }

        while (_pendingRemoteCandidates.TryDequeue(out VoiceIceCandidate? candidate))
        {
            try
            {
                await engine.AddRemoteIceCandidateAsync(candidate).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Events.Publish(new VoiceDiagnosticMessage($"⚠ Ошибка добавления накопленного ICE-кандидата: {ex.Message}"));
            }
        }
    }

    private void EndChatRemote(string? connectionId, bool soft)
    {
        if (!string.IsNullOrEmpty(connectionId) && _peerConnectionId != null && connectionId != _peerConnectionId)
        {
            Events.Publish(new VoiceDiagnosticMessage($"⚠ Игнорируем отключение от прошлого собеседника ({connectionId})"));
            return;
        }

        string id = connectionId ?? _peerConnectionId ?? string.Empty;
        bool hadMedia = _mediaEstablished;
        CloseEngine();
        if (soft)
        {
            _resumePeerId = _peerConnectionId ?? connectionId;
        }

        _peerConnectionId = null;
        _mediaEstablished = false;
        _muted = false;
        if (hadMedia)
        {
            _ = SendAsync(VoiceCodec.PeerConnection(false));
        }
        Events.Publish(new VoicePeerGone(id, soft));
    }

    private async Task EndChatAsync(bool sendDisconnect, bool soft, CancellationToken cancellationToken)
    {
        string? id = _peerConnectionId;
        bool hadMedia = _mediaEstablished;
        if (sendDisconnect && id is not null)
        {
            await SendAsync(VoiceCodec.PeerDisconnect(id), cancellationToken).ConfigureAwait(false);
        }

        CloseEngine();
        _peerConnectionId = null;
        _mediaEstablished = false;
        _muted = false;
        if (hadMedia)
        {
            await SendAsync(VoiceCodec.PeerConnection(false), cancellationToken).ConfigureAwait(false);
        }
        if (id is not null)
        {
            Events.Publish(new VoicePeerGone(id, soft));
        }
    }

    private void HandleError(VoiceWireEvent e)
    {
        _searching = false;
        string? code = null;
        string? description = null;

        if (e.Root.TryGetProperty("error", out JsonElement errObj))
        {
            if (errObj.ValueKind == JsonValueKind.Object)
            {
                if (errObj.TryGetProperty("code", out JsonElement c))
                    code = c.ToString();
                if (errObj.TryGetProperty("text", out JsonElement t))
                    description = t.GetString();
                else if (errObj.TryGetProperty("message", out JsonElement m))
                    description = m.GetString();
                else if (errObj.TryGetProperty("description", out JsonElement d))
                    description = d.GetString();
            }
            else if (errObj.ValueKind == JsonValueKind.String)
            {
                description = errObj.GetString();
            }
        }
        else
        {
            try
            {
                VoiceErrorData data = VoiceCodec.Parse<VoiceErrorData>(e);
                code = data.Code;
                description = data.Description ?? data.Message;
            }
            catch (JsonException)
            {
                // Unrecognized error shape; the raw payload becomes the description.
            }
        }

        description ??= Truncate(e.Root.GetRawText());

        // Check for ban error codes (code 201 is IP ban on Nekto.me) or ban descriptions
        bool isBan = code == "201" ||
                     (description != null && (description.Contains("заблокирован", StringComparison.OrdinalIgnoreCase) ||
                                              description.Contains("banned", StringComparison.OrdinalIgnoreCase)));

        if (isBan)
        {
            _identity.ResetIdentity();
            Events.Publish(new VoiceBanned(
                BanEnum: code == "201" ? "IP_BLOCKED" : "ERROR_BAN",
                Permanently: true,
                Text: description,
                Detail: e.Root.GetRawText()));
            return;
        }

        Events.Publish(new VoiceErrorReceived(
            code, description ?? (code is null ? Truncate(e.Root.GetRawText()) : null)));
    }

    private IAudioEngine RequireEngine() =>
        _engine ?? throw new InvalidOperationException("No media engine for the active chat.");

    private async Task<AudioServerEndpoint> ResolveEndpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _endpoints.ResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (_options.FallbackEndpoint is not null)
        {
            return _options.FallbackEndpoint;
        }
    }

    private async Task SendAsync(VoiceWireEvent message, CancellationToken cancellationToken = default)
    {
        try
        {
            await _transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Socket or synchronization lock disposed during disconnect
        }
        catch (OperationCanceledException)
        {
            // Teardown during send
        }
    }

    private void CloseEngine()
    {
        CancelWatchdog();
        CancelIceConnectTimeout();
        _remoteDescriptionSet = false;
        _pendingRemoteCandidates.Clear();
        _localCandidateKinds.Clear();
        _remoteCandidateKinds.Clear();
        IAudioEngine? engine = _engine;
        _engine = null;
        if (engine is null)
        {
            return;
        }

        UnhookEngine(engine);
        engine.Close();
        engine.Dispose();
        _microphone?.Stop();
    }

    private static string? Truncate(string? text) =>
        string.IsNullOrEmpty(text) ? text : text.Length <= 300 ? text : text[..300];

    /// <summary>Extracts the candidate type (host/srflx/relay/prflx) from an SDP candidate string.</summary>
    private static string CandidateKind(string? candidate)
    {
        Match match = Regex.Match(candidate ?? string.Empty, @"typ\s+(\w+)");
        return match.Success ? match.Groups[1].Value : "?";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_microphone is not null)
        {
            _microphone.PcmCaptured -= OnMicrophonePcm;
        }

        CloseEngine();
        CancelWatchdog();
        CancelIceConnectTimeout();
        _transport.Dispose();
        _dispatchGate.Dispose();
    }
}
