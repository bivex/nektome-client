using System.Text.Json;
using NektoMe.Application.Abstractions;
using NektoMe.Application.Events;
using NektoMe.Application.Protocol;
using NektoMe.Application.Services;

namespace NektoMe.Application.Tests;

public sealed class VoiceChatSessionTests
{
    private const string RegisteredJson =
        """{"type":"registered","success":true,"connectionId":"conn-1","internal_id":42}""";

    private const string PeerConnectAnswererJson =
        """{"type":"peer-connect","connectionId":"p1","initiator":false,"relay":true,"time":1700000000,"stunUrl":"stun:stun.example:3478","turnParams":[{"url":"turn:turn.example:3478","username":"u1","credential":"c1"}]}""";

    private const string PeerConnectInitiatorJson =
        """{"type":"peer-connect","connectionId":"p2","initiator":true,"relay":false}""";

    private static string OfferJson(string sdp = "v=0 remote offer") =>
        "{\"type\":\"offer\",\"connectionId\":\"p1\",\"offer\":" +
        JsonSerializer.Serialize(JsonSerializer.Serialize(new SdpDescription("offer", sdp))) + "}";

    private static string AnswerJson(string sdp = "v=0 remote answer", string connectionId = "p1") =>
        "{\"type\":\"answer\",\"connectionId\":\"" + connectionId + "\",\"answer\":" +
        JsonSerializer.Serialize(JsonSerializer.Serialize(new SdpDescription("answer", sdp))) + "}";

    private static string IceJson(string candidate = "candidate:1 1 udp 1 10.0.0.1 5000 typ host") =>
        "{\"type\":\"ice-candidate\",\"connectionId\":\"p1\",\"candidate\":" +
        JsonSerializer.Serialize(JsonSerializer.Serialize(
            new IceCandidateDescription(new VoiceCandidateData("0", 0, candidate)))) + "}";

    private static JsonElement Payload(FakeVoiceTransport transport, int index = -1)
    {
        VoiceWireEvent e = transport.Sent[index < 0 ? ^1 : Index.FromStart(index)];
        return e.Root;
    }

    // -- microphone -------------------------------------------------------------

    [Fact]
    public async Task Microphone_starts_on_remote_audio_and_feeds_the_engine()
    {
        var microphone = new FakeMicrophoneCapture();
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create(microphone: microphone);
        await session.ConnectAsync();
        transport.SimulateEvent(RegisteredJson);
        transport.SimulateEvent(PeerConnectAnswererJson);

        Assert.False(microphone.IsCapturing);
        engines[^1].RaiseRemoteAudioStarted();
        session.MicDspEnabled = false;
        session.MicGain = 1.0f;

        Assert.True(microphone.IsCapturing);
        Assert.Equal(1, microphone.StartCount);

        microphone.RaisePcm(1, 2, 3);
        short[] pushed = Assert.Single(engines[^1].PushedPcm!);
        Assert.Equal(new short[] { 1, 2, 3 }, pushed);
    }

    [Fact]
    public async Task Microphone_applies_mic_gain_scaling()
    {
        var microphone = new FakeMicrophoneCapture();
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create(microphone: microphone);
        await session.ConnectAsync();
        transport.SimulateEvent(RegisteredJson);
        transport.SimulateEvent(PeerConnectAnswererJson);
        engines[^1].RaiseRemoteAudioStarted();
        session.MicDspEnabled = false;

        session.MicGain = 1.5f;
        microphone.RaisePcm(100, 200, -300);

        short[] pushed = Assert.Single(engines[^1].PushedPcm!);
        Assert.Equal(new short[] { 150, 300, -450 }, pushed);
    }

    [Fact]
    public async Task Muted_microphone_frames_do_not_reach_the_engine()
    {
        var microphone = new FakeMicrophoneCapture();
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create(microphone: microphone);
        await session.ConnectAsync();
        transport.SimulateEvent(RegisteredJson);
        transport.SimulateEvent(PeerConnectAnswererJson);
        engines[^1].RaiseRemoteAudioStarted();
        session.MicDspEnabled = false;
        session.MicGain = 1.0f;

        await session.SetMutedAsync(true);
        microphone.RaisePcm(1, 2, 3);
        Assert.Null(engines[^1].PushedPcm);

        await session.SetMutedAsync(false);
        microphone.RaisePcm(4, 5);
        short[] pushed = Assert.Single(engines[^1].PushedPcm!);
        Assert.Equal(new short[] { 4, 5 }, pushed);
    }

    [Fact]
    public async Task Microphone_stops_when_the_peer_goes_away()
    {
        var microphone = new FakeMicrophoneCapture();
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create(microphone: microphone);
        await session.ConnectAsync();
        transport.SimulateEvent(RegisteredJson);
        transport.SimulateEvent(PeerConnectAnswererJson);
        engines[^1].RaiseRemoteAudioStarted();
        Assert.True(microphone.IsCapturing);

        transport.SimulateEvent("""{"type":"peer-disconnect","connectionId":"p1"}""");

        Assert.False(microphone.IsCapturing);
        Assert.Equal(1, microphone.StopCount);

        microphone.RaisePcm(1, 2, 3);
        Assert.Null(engines[^1].PushedPcm);
    }

    // -- connect / register ----------------------------------------------------

    [Fact]
    public async Task Connect_resolves_endpoint_and_connects_transport()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        await session.ConnectAsync();
        Assert.True(transport.IsConnected);
        Assert.Equal(VoiceTestHarness.Endpoint, transport.LastEndpoint);
    }

    [Fact]
    public async Task Connect_uses_fallback_endpoint_when_resolver_throws()
    {
        var fallback = new AudioServerEndpoint(new Uri("wss://fallback.example/"), "/fb");
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create(
            options: new VoiceOptions { FallbackEndpoint = fallback },
            resolver: new ThrowingEndpointResolver());
        await session.ConnectAsync();
        Assert.Equal(fallback, transport.LastEndpoint);
    }

    [Fact]
    public async Task Transport_connect_triggers_register_with_identity_and_locale()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        await session.ConnectAsync();

        Assert.Equal(VoiceWireNames.Register, transport.LastSentType);
        JsonElement payload = Payload(transport);
        Assert.True(payload.GetProperty("android").GetBoolean());
        Assert.Equal("desktop-voice-1", payload.GetProperty("userId").GetString());
        Assert.Equal("en", payload.GetProperty("locale").GetString());
        Assert.Equal("Europe/Moscow", payload.GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task Transport_connect_sends_last_persisted_connection_id_on_first_register()
    {
        var identity = new StaticVoiceIdentity("user-1", initialConnectionId: "saved-conn-123");
        (VoiceChatSession session, FakeVoiceTransport transport, _) =
            VoiceTestHarness.Create(identity: identity);

        await session.ConnectAsync();

        Assert.Equal(VoiceWireNames.Register, transport.LastSentType);
        JsonElement payload = Payload(transport);
        Assert.Equal("saved-conn-123", payload.GetProperty("connectionId").GetString());
    }

    [Fact]
    public async Task Registered_event_persists_connection_id_to_identity()
    {
        var identity = new StaticVoiceIdentity("user-1");
        (VoiceChatSession session, FakeVoiceTransport transport, _) =
            VoiceTestHarness.Create(identity: identity);

        await session.ConnectAsync();
        transport.SimulateEvent(RegisteredJson);

        Assert.Equal("conn-1", identity.LastSavedConnectionId);
        Assert.Equal("conn-1", identity.GetLastConnectionId());
    }

    [Fact]
    public async Task Soft_disconnect_opens_resume_window_for_next_register_once()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create();
        await session.ConnectAsync();
        transport.SimulateEvent(RegisteredJson); // establishes _connectionId
        transport.SimulateEvent(PeerConnectAnswererJson);
        transport.SimulateEvent("""{"type":"peer-soft-disconnect","connectionId":"p1"}""");
        Assert.Single(engines);
        Assert.True(engines[0].Closed);

        // Reconnect: register must repeat the peer id as peerSuccess exactly once.
        transport.SimulateConnect();
        JsonElement resumePayload = Payload(transport);
        Assert.Equal("conn-1", resumePayload.GetProperty("connectionId").GetString());
        Assert.Equal("p1", resumePayload.GetProperty("peerSuccess").GetString());

        transport.SimulateConnect();
        Assert.Equal("conn-1", Payload(transport).GetProperty("connectionId").GetString());
        Assert.False(Payload(transport).TryGetProperty("peerSuccess", out JsonElement _));
    }

    // -- registered / users count ------------------------------------------------

    [Fact]
    public async Task Registered_success_publishes_event_and_requests_users_count()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent(RegisteredJson);

        VoiceRegistered registered = events.OfType<VoiceRegistered>().Single();
        Assert.True(registered.Success);
        Assert.Equal("conn-1", registered.ConnectionId);
        // Like official Android APK, users-count is pushed by server, not requested.
        Assert.Equal(VoiceWireNames.Register, transport.LastSentType);
    }

    [Fact]
    public async Task Registered_failure_publishes_error_code_without_users_count()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.Sent.Clear();
        transport.SimulateEvent("""{"type":"registered","success":false,"errorCode":4}""");

        VoiceRegistered registered = events.OfType<VoiceRegistered>().Single();
        Assert.False(registered.Success);
        Assert.Equal(4, registered.ErrorCode);
        Assert.DoesNotContain(transport.Sent, m => m.Type == VoiceWireNames.UsersCountRequest);
    }

    [Fact]
    public async Task Registered_parses_numeric_error_code_from_live_server()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent("""{"type":"registered","success":true,"connectionId":"c9","errorCode":0,"internal_id":7,"premium":false}""");

        VoiceRegistered registered = events.OfType<VoiceRegistered>().Single();
        Assert.True(registered.Success);
        Assert.Equal("c9", registered.ConnectionId);
        Assert.Equal(0, registered.ErrorCode);
    }

    // -- ping-server-request ------------------------------------------------------

    [Fact]
    public async Task Ping_server_request_replies_with_probed_results()
    {
        var prober = new FakePingProber(
        [
            new VoicePingSample("a.example", 12, 0),
            new VoicePingSample("b.example", -1, 3),
        ]);
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create(pingProber: prober);
        await session.ConnectAsync();
        transport.Sent.Clear();

        transport.SimulateEvent("""{"type":"ping-server-request","attempts":3,"list":["a.example","b.example"]}""");

        (IReadOnlyList<string> Servers, int Attempts) call = Assert.Single(prober.Calls);
        Assert.Equal(["a.example", "b.example"], call.Servers);
        Assert.Equal(3, call.Attempts);

        VoiceWireEvent reply = transport.Sent.Single(m => m.Type == VoiceWireNames.LogPingResults);
        JsonElement result = reply.Root.GetProperty("result");
        Assert.Equal(2, result.GetArrayLength());
        Assert.Equal("a.example", result[0].GetProperty("server").GetString());
        Assert.Equal(12, result[0].GetProperty("ping").GetInt32());
        Assert.Equal(0, result[0].GetProperty("fails").GetInt32());
        Assert.Equal(-1, result[1].GetProperty("ping").GetInt32());
        Assert.Equal(3, result[1].GetProperty("fails").GetInt32());
    }

    [Fact]
    public async Task Ping_server_request_without_prober_reports_unreachable()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        await session.ConnectAsync();
        transport.Sent.Clear();

        transport.SimulateEvent("""{"type":"ping-server-request","attempts":4,"list":["solo.example"]}""");

        VoiceWireEvent reply = transport.Sent.Single(m => m.Type == VoiceWireNames.LogPingResults);
        JsonElement result = reply.Root.GetProperty("result");
        Assert.Equal(1, result.GetArrayLength());
        Assert.Equal("solo.example", result[0].GetProperty("server").GetString());
        Assert.Equal(-1, result[0].GetProperty("ping").GetInt32());
        Assert.Equal(4, result[0].GetProperty("fails").GetInt32());
    }

    // -- search ------------------------------------------------------------------

    [Fact]
    public async Task StartSearch_sends_criteria_without_token()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        await session.ConnectAsync();
        await session.StartSearchAsync(new VoiceSearchCriteria(UserSex: "MALE", PeerSex: "FEMALE", PeerAges: [new VoiceAgeRange(18, 21)]));

        Assert.Equal(VoiceWireNames.ScanForPeer, transport.LastSentType);
        JsonElement payload = Payload(transport);
        JsonElement criteria = payload.GetProperty("searchCriteria");
        Assert.Equal(0, criteria.GetProperty("group").GetInt32());
        Assert.Equal("MALE", criteria.GetProperty("userSex").GetString());
        Assert.Equal("FEMALE", criteria.GetProperty("peerSex").GetString());
        Assert.Equal(18, criteria.GetProperty("peerAges")[0].GetProperty("from").GetInt32());
        Assert.Equal(21, criteria.GetProperty("peerAges")[0].GetProperty("to").GetInt32());
        Assert.True(payload.GetProperty("peerToPeer").GetBoolean());
        Assert.False(payload.TryGetProperty("token", out JsonElement _));
    }

    [Fact]
    public async Task StartSearch_with_captcha_token_sends_image_token_type()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        await session.ConnectAsync();
        await session.SubmitCaptchaAsync("tok-123");

        JsonElement payload = Payload(transport);
        Assert.Equal("tok-123", payload.GetProperty("token").GetString());
        Assert.Equal("IMAGE", payload.GetProperty("tokenType").GetString());
    }

    [Fact]
    public async Task StopSearch_sends_stop_scan_and_publishes_stopped_event()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable sub = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        await session.StartSearchAsync();
        Assert.Contains(events, e => e is VoiceSearchStateChanged { Searching: true });

        events.Clear();
        await session.StopSearchAsync();
        Assert.Equal(VoiceWireNames.StopScan, transport.LastSentType);
        Assert.Contains(events, e => e is VoiceSearchStateChanged { Searching: false });
    }

    [Fact]
    public async Task SearchOut_event_stops_search_and_publishes_stopped_event()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable sub = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        await session.StartSearchAsync();
        Assert.Contains(events, e => e is VoiceSearchStateChanged { Searching: true });

        events.Clear();
        transport.SimulateEvent("""{"type":"search.out"}""");
        Assert.Contains(events, e => e is VoiceSearchStateChanged { Searching: false });
    }

    // -- peer-connect / answerer path ---------------------------------------------

    [Fact]
    public async Task Peer_connect_answerer_builds_engine_with_turn_servers_and_answers_offer()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent(PeerConnectAnswererJson);
        Assert.Empty(engines[^1].RemoteDescriptions);
        Assert.Equal(0, engines[^1].OfferCount);

        transport.SimulateEvent(OfferJson());
        FakeAudioEngine engine = engines[^1];
        Assert.Single(engine.RemoteDescriptions);
        Assert.Equal(("offer", "v=0 remote offer"), engine.RemoteDescriptions[0]);
        Assert.Equal(1, engine.AnswerCount);
        Assert.Equal(VoiceWireNames.Answer, transport.LastSentType);

        JsonElement answerPayload = Payload(transport);
        Assert.Equal("p1", answerPayload.GetProperty("connectionId").GetString());
        string answerSdp = answerPayload.GetProperty("answer").GetString()!;
        using JsonDocument doc = JsonDocument.Parse(answerSdp);
        Assert.Equal("answer", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("answer-sdp-1", doc.RootElement.GetProperty("sdp").GetString());

        VoicePeerFound found = events.OfType<VoicePeerFound>().Single();
        Assert.Equal("p1", found.ConnectionId);
        Assert.False(found.Initiator);
        Assert.True(found.Relay);
    }

    [Fact]
    public async Task Engine_receives_turn_and_stun_servers_from_peer_connect()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create();
        await session.ConnectAsync();
        transport.SimulateEvent(PeerConnectAnswererJson);

        FakeAudioEngine engine = engines[^1];
        Assert.NotNull(engine.CreatedWithServers);
        Assert.Equal(2, engine.CreatedWithServers!.Count);
        Assert.Contains(engine.CreatedWithServers, s => s.Url == "turn:turn.example:3478" && s.Username == "u1" && s.Credential == "c1");
        Assert.Contains(engine.CreatedWithServers, s => s.Url == "stun:stun.example:3478");
        Assert.True(engine.CreatedRelayOnly);
    }

    // -- peer-connect / initiator path ---------------------------------------------

    [Fact]
    public async Task Peer_connect_initiator_sends_offer_and_applies_remote_answer()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create();
        await session.ConnectAsync();
        transport.SimulateEvent(PeerConnectInitiatorJson);

        FakeAudioEngine engine = engines[^1];
        Assert.Equal(1, engine.OfferCount);
        Assert.Equal(VoiceWireNames.Offer, transport.LastSentType);
        JsonElement offerPayload = Payload(transport);
        Assert.Equal("p2", offerPayload.GetProperty("connectionId").GetString());
        using JsonDocument offerDoc = JsonDocument.Parse(offerPayload.GetProperty("offer").GetString()!);
        Assert.Equal("offer", offerDoc.RootElement.GetProperty("type").GetString());

        transport.SimulateEvent(AnswerJson(connectionId: "p2"));
        Assert.Single(engine.RemoteDescriptions);
        Assert.Equal(("answer", "v=0 remote answer"), engine.RemoteDescriptions[0]);
        Assert.Equal(0, engine.AnswerCount);
        // Reference APK parity: peer-mute is deferred until the media is up,
        // it must not ride along with the answer.
        Assert.DoesNotContain(transport.Sent, e => e.Type == VoiceWireNames.PeerMute);

        engine.RaiseIceState("connected");
        Assert.Contains(transport.Sent, e => e.Type == VoiceWireNames.StreamReceived);
        Assert.Contains(transport.Sent, e => e.Type == VoiceWireNames.PeerConnection);
        Assert.Contains(transport.Sent, e => e.Type == VoiceWireNames.PeerMute);
    }

    // -- ICE ------------------------------------------------------------------------

    [Fact]
    public async Task Local_ice_candidates_are_forwarded_with_connection_id()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create();
        await session.ConnectAsync();
        transport.SimulateEvent(PeerConnectAnswererJson);
        transport.Sent.Clear();

        engines[^1].RaiseLocalCandidate("0", 1, "candidate:local");

        Assert.Equal(VoiceWireNames.IceCandidate, transport.LastSentType);
        JsonElement payload = Payload(transport);
        Assert.Equal("p1", payload.GetProperty("connectionId").GetString());
        using JsonDocument doc = JsonDocument.Parse(payload.GetProperty("candidate").GetString()!);
        Assert.Equal("candidate:local", doc.RootElement.GetProperty("candidate").GetProperty("candidate").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("candidate").GetProperty("sdpMLineIndex").GetInt32());
    }

    [Fact]
    public async Task Remote_candidates_before_remote_description_are_buffered_then_flushed()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create();
        await session.ConnectAsync();
        transport.SimulateEvent(PeerConnectAnswererJson);

        transport.SimulateEvent(IceJson("candidate:first"));
        transport.SimulateEvent(IceJson("candidate:second"));
        Assert.Empty(engines[^1].RemoteCandidates);

        transport.SimulateEvent(OfferJson());
        Assert.Equal(["candidate:first", "candidate:second"],
            engines[^1].RemoteCandidates.Select(c => c.Candidate).ToArray());

        transport.SimulateEvent(IceJson("candidate:live"));
        Assert.Equal(3, engines[^1].RemoteCandidates.Count);
        Assert.Equal("candidate:live", engines[^1].RemoteCandidates[^1].Candidate);
    }

    // -- media lifecycle --------------------------------------------------------------

    [Fact]
    public async Task Remote_audio_start_sends_stream_received_and_marks_media_established()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent(PeerConnectAnswererJson);
        transport.Sent.Clear();
        engines[^1].RaiseRemoteAudioStarted();

        Assert.Equal(3, transport.Sent.Count);
        Assert.Equal(VoiceWireNames.StreamReceived, transport.Sent[0].Type);
        Assert.Equal("p1", Payload(transport, 0).GetProperty("connectionId").GetString());

        Assert.Equal(VoiceWireNames.PeerConnection, transport.Sent[1].Type);
        Assert.Equal("p1", Payload(transport, 1).GetProperty("connectionId").GetString());
        Assert.True(Payload(transport, 1).GetProperty("connection").GetBoolean());

        Assert.Equal(VoiceWireNames.PeerMute, transport.Sent[2].Type);
        Assert.Equal("p1", Payload(transport, 2).GetProperty("connectionId").GetString());
        Assert.False(Payload(transport, 2).GetProperty("muted").GetBoolean());

        Assert.True(session.MediaEstablished);
        Assert.Contains(events, e => e is VoiceMediaEstablished { ConnectionId: "p1" });
    }

    [Fact]
    public async Task Peer_mute_from_server_is_published()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent("""{"type":"peer-mute","connectionId":"p1","muted":true}""");
        Assert.Contains(events, e => e is VoicePeerMuteChanged { Muted: true });
    }

    [Fact]
    public async Task Mute_updates_engine_and_notifies_peer()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create();
        await session.ConnectAsync();
        transport.SimulateEvent(PeerConnectAnswererJson);
        transport.Sent.Clear();

        await session.SetMutedAsync(true);
        Assert.Equal([true], engines[^1].MuteCalls);
        Assert.Equal(VoiceWireNames.PeerMute, transport.LastSentType);
        Assert.True(Payload(transport).GetProperty("muted").GetBoolean());
    }

    [Fact]
    public async Task Peer_disconnect_closes_engine_and_publishes_gone()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent(PeerConnectAnswererJson);
        transport.SimulateEvent("""{"type":"peer-disconnect","connectionId":"p1"}""");

        Assert.True(engines[^1].Closed);
        Assert.False(session.InChat);
        VoicePeerGone gone = events.OfType<VoicePeerGone>().Single();
        Assert.Equal("p1", gone.ConnectionId);
        Assert.False(gone.Soft);
    }

    [Fact]
    public async Task Hangup_sends_peer_disconnect_and_closes_engine()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent(PeerConnectInitiatorJson);
        transport.Sent.Clear();

        await session.HangupAsync();

        Assert.Equal(VoiceWireNames.PeerDisconnect, transport.LastSentType);
        Assert.True(engines[^1].Closed);
        Assert.False(session.InChat);
        Assert.Contains(events, e => e is VoicePeerGone { ConnectionId: "p2", Soft: false });
    }

    // -- counters / captcha / errors ----------------------------------------------------

    [Fact]
    public async Task Users_count_is_published()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent(
            """{"type":"users-count","usersCount":10,"waitingUsersCount":3,"talkingUsersCount":2}""");

        VoiceUsersCount count = events.OfType<VoiceUsersCount>().Single();
        Assert.Equal((10, 3, 2), (count.Users, count.Waiting, count.Talking));
    }

    [Fact]
    public async Task Captcha_request_is_published_and_token_resumes_search()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent(
            """{"type":"captcha-request","captchaType":"IMAGE","leftChats":1,"needChats":2,"url":"https://captcha"}""");

        VoiceCaptchaRequested captcha = events.OfType<VoiceCaptchaRequested>().Single();
        Assert.Equal("IMAGE", captcha.CaptchaType);
        Assert.Equal("https://captcha", captcha.Url);
        Assert.Equal((1, 2), (captcha.LeftChats, captcha.NeedChats));

        transport.Sent.Clear();
        await session.SubmitCaptchaAsync("solved-token");
        Assert.Equal(VoiceWireNames.ScanForPeer, transport.LastSentType);
        Assert.Equal("solved-token", Payload(transport).GetProperty("token").GetString());
    }

    [Fact]
    public async Task Ban_event_is_parsed_into_ban_info()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent(
            """{"type":"ban","banInfo":{"banEnum":"BAN","permanently":true,"text":"Доступ к чату запрещён"}}""");

        VoiceBanned ban = events.OfType<VoiceBanned>().Single();
        Assert.Equal("BAN", ban.BanEnum);
        Assert.True(ban.Permanently);
        Assert.Equal("Доступ к чату запрещён", ban.Text);
        Assert.Contains("\"banEnum\":\"BAN\"", ban.Detail);
    }

    [Fact]
    public async Task Ban_event_without_baninfo_object_falls_back_to_root()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent("""{"type":"ban","banEnum":"IP","permanently":false,"text":"slow down"}""");

        VoiceBanned ban = events.OfType<VoiceBanned>().Single();
        Assert.Equal("IP", ban.BanEnum);
        Assert.False(ban.Permanently);
        Assert.Equal("slow down", ban.Text);
    }

    [Fact]
    public async Task Error_event_is_parsed_into_code_and_description()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent("""{"type":"error","code":"LIMIT","description":"too many"}""");
        transport.SimulateEvent("""{"type":"error","message":"weird"}""");

        List<VoiceErrorReceived> errors = events.OfType<VoiceErrorReceived>().ToList();
        Assert.Equal(2, errors.Count);
        Assert.Equal(("LIMIT", "too many"), (errors[0].Code, errors[0].Description));
        Assert.Null(errors[1].Code);
        Assert.Equal("weird", errors[1].Description);
    }

    [Fact]
    public async Task Unknown_events_surface_as_raw()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent("""{"type":"purchase-changed","paidType":3}""");
        Assert.Contains(events, e => e is VoicePurchaseChanged { PaidType: 3 });
        transport.SimulateEvent("""{"type":"totally-new","x":1}""");
        Assert.Contains(events, e => e is VoiceRawEvent { Type: "totally-new" });
    }

    // -- transport failures -----------------------------------------------------------

    [Fact]
    public async Task Transport_disconnect_resets_chat_state_and_publishes_connection_changed()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create();
        var events = new List<VoiceChatEvent>();
        using IDisposable subscription = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent(PeerConnectAnswererJson);
        transport.SimulateDisconnect("io timeout");

        Assert.True(engines[^1].Closed);
        Assert.False(session.InChat);
        VoiceConnectionChanged changed = events.OfType<VoiceConnectionChanged>().Last();
        Assert.False(changed.Connected);
        Assert.Equal("io timeout", changed.Reason);
    }

    [Fact]
    public async Task Reconnect_during_search_resends_scan_after_register()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        await session.ConnectAsync();
        await session.StartSearchAsync();
        transport.SimulateDisconnect();
        transport.SimulateConnect(); // register goes out
        transport.SimulateEvent(RegisteredJson);

        // The scan must follow the successful re-registration.
        Assert.Equal(VoiceWireNames.ScanForPeer, transport.LastSentType);
    }

    [Fact]
    public async Task Connect_before_any_chat_is_safe_to_disconnect()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create();
        await session.ConnectAsync();
        await session.DisconnectAsync();
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task Verify_SocketIo_Handshake_Headers()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var tcs = new TaskCompletionSource<string>();
        _ = Task.Run(async () =>
        {
            using System.Net.Sockets.TcpClient client = await listener.AcceptTcpClientAsync();
            using System.IO.Stream stream = client.GetStream();
            byte[] buffer = new byte[4096];
            int read = await stream.ReadAsync(buffer);
            string request = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            tcs.SetResult(request);
        });

        var options = new VoiceOptions();
        var transportOptions = new NektoMe.Infrastructure.Transports.VoiceTransportOptions
        {
            Reconnection = false,
        };
        var transport = new NektoMe.Infrastructure.Transports.SocketIoVoiceTransport(options, transportOptions);
        var endpoint = new AudioServerEndpoint(new Uri($"http://127.0.0.1:{port}/"), "/socket");
        try
        {
            _ = transport.ConnectAsync(endpoint);
            string received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("NektoMe-Chat-Version", received);
            Assert.Contains("App-Android-Version", received);
            Assert.Contains("App-Android-Code", received);
            Assert.Contains("Android-Language", received);
            Assert.Contains("User-Agent", received);
        }
        finally
        {
            listener.Stop();
            await transport.DisconnectAsync();
        }
    }

    [Fact]
    public async Task Peer_connect_with_string_encoded_turn_params_parses_ice_servers()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create();
        await session.ConnectAsync();

        const string stringTurnParamsEvent =
            """{"type":"peer-connect","connectionId":"3446162963","initiator":false,"turnParams":"[{\"credential\":\"cred1\",\"url\":\"turn:[2a01:4f8:c015:8a6c::1]:3478\",\"username\":\"u1\"},{\"credential\":\"cred2\",\"url\":\"turn:turn-def.nekto.me:3478\",\"username\":\"u2\"}]","stunUrl":"stun:stun.example:3478","time":0,"relay":true}""";

        transport.SimulateEvent(stringTurnParamsEvent);

        Assert.True(session.InChat);
        FakeAudioEngine engine = Assert.Single(engines);
        Assert.NotNull(engine.CreatedWithServers);
        Assert.Equal(3, engine.CreatedWithServers.Count);
        Assert.Equal("turn:[2a01:4f8:c015:8a6c::1]:3478", engine.CreatedWithServers[0].Url);
        Assert.Equal("u1", engine.CreatedWithServers[0].Username);
        Assert.Equal("cred1", engine.CreatedWithServers[0].Credential);
        Assert.Equal("turn:turn-def.nekto.me:3478", engine.CreatedWithServers[1].Url);
        Assert.Equal("stun:stun.example:3478", engine.CreatedWithServers[2].Url);
    }

    [Fact]
    public async Task End_chat_after_media_established_sends_peer_connection_false()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create();
        await session.ConnectAsync();
        transport.SimulateEvent(PeerConnectAnswererJson);
        engines[^1].RaiseRemoteAudioStarted();
        transport.Sent.Clear();

        await session.HangupAsync();

        Assert.Equal(2, transport.Sent.Count);
        Assert.Equal(VoiceWireNames.PeerDisconnect, transport.Sent[0].Type);
        Assert.Equal(VoiceWireNames.PeerConnection, transport.Sent[1].Type);
        Assert.False(Payload(transport, 1).GetProperty("connection").GetBoolean());
        Assert.False(Payload(transport, 1).TryGetProperty("connectionId", out _));
    }

    [Fact]
    public async Task Peer_connect_watchdog_timeout_recovers_stalled_peer_and_resumes_search()
    {
        var options = new VoiceOptions { PeerConnectTimeoutSeconds = 1 };
        (VoiceChatSession session, FakeVoiceTransport transport, List<FakeAudioEngine> engines) =
            VoiceTestHarness.Create(options: options);
        var events = new List<VoiceChatEvent>();
        using IDisposable sub = session.Events.Subscribe(events.Add);

        await session.ConnectAsync();
        transport.SimulateEvent(PeerConnectAnswererJson);
        Assert.True(session.InChat);
        transport.Sent.Clear();

        // Wait slightly more than 1 second for watchdog to fire
        await Task.Delay(1300);

        Assert.False(session.InChat);
        Assert.True(engines[^1].Closed);
        Assert.Contains(transport.Sent, m => m.Type == VoiceWireNames.PeerDisconnect);
        Assert.Contains(transport.Sent, m => m.Type == VoiceWireNames.ScanForPeer);
        Assert.Contains(events, e => e is VoicePeerGone { ConnectionId: "p1" });
        Assert.Contains(events, e => e is VoiceSearchStateChanged { Searching: true });
    }

    [Fact]
    public async Task Web_mode_challenge_request_automatically_replies_with_challenge_proof()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create(
            options: new VoiceOptions { ProtocolMode = VoiceProtocolMode.Web });
        await session.ConnectAsync();
        transport.Sent.Clear();

        transport.SimulateEvent("""{"type":"challenge-request","challengeId":"test-cid","stamp":1700000000,"echo":"echo-val"}""");

        Assert.Equal("challenge-proof", transport.LastSentType);
        JsonElement payload = Payload(transport);
        Assert.Equal("test-cid", payload.GetProperty("challengeId").GetString());
        Assert.Equal(1700000000, payload.GetProperty("stamp").GetInt64());
        Assert.Equal("echo-val", payload.GetProperty("echo").GetString());
        Assert.True(payload.TryGetProperty("proof", out _));
        Assert.True(payload.TryGetProperty("checksum", out _));
    }

    [Fact]
    public async Task Web_mode_challenge_sync_automatically_replies_with_challenge_ack()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create(
            options: new VoiceOptions { ProtocolMode = VoiceProtocolMode.Web });
        await session.ConnectAsync();
        transport.Sent.Clear();

        transport.SimulateEvent("""{"type":"challenge-sync","challengeId":"cid-sync","stamp":1700000000}""");

        Assert.Equal("challenge-ack", transport.LastSentType);
        JsonElement payload = Payload(transport);
        Assert.Equal("cid-sync", payload.GetProperty("challengeId").GetString());
        Assert.Equal(1700000000, payload.GetProperty("stamp").GetInt64());
    }

    [Fact]
    public async Task Web_mode_challenge_trace_automatically_replies_with_challenge_trace()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create(
            options: new VoiceOptions { ProtocolMode = VoiceProtocolMode.Web });
        await session.ConnectAsync();
        transport.Sent.Clear();

        transport.SimulateEvent("""{"type":"challenge-trace","challengeId":"cid-trace","stamp":1700000000,"signal":"hold"}""");

        Assert.Equal("challenge-trace", transport.LastSentType);
        JsonElement payload = Payload(transport);
        Assert.Equal("cid-trace", payload.GetProperty("challengeId").GetString());
        Assert.Equal("hold", payload.GetProperty("signal").GetString());
        Assert.True(payload.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Web_mode_ping_with_echo_automatically_replies_with_ping_server_response()
    {
        (VoiceChatSession session, FakeVoiceTransport transport, _) = VoiceTestHarness.Create(
            options: new VoiceOptions { ProtocolMode = VoiceProtocolMode.Web });
        await session.ConnectAsync();
        transport.Sent.Clear();

        transport.SimulateEvent("""{"type":"ping-server-request","echo":{"token":"123","time":999}}""");

        Assert.Equal("ping-server-response", transport.LastSentType);
        JsonElement payload = Payload(transport);
        Assert.Equal("123", payload.GetProperty("echo").GetProperty("token").GetString());
    }
}


