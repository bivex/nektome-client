using System.Text.Json;
using NektoMe.Application.Abstractions;
using NektoMe.Application.Protocol;

namespace NektoMe.Application.Tests;

public sealed class VoiceCodecTests
{
    [Fact]
    public void Register_carries_identity_locale_and_resume_fields()
    {
        var options = new VoiceOptions { Locale = "ru", TimeZone = "Europe/Moscow" };
        VoiceWireEvent wire = VoiceCodec.Register("user-7", "conn-prev", "peer-prev", options);

        Assert.Equal("register", wire.Type);
        JsonElement root = wire.Root;
        Assert.True(root.GetProperty("android").GetBoolean());
        Assert.Equal("user-7", root.GetProperty("userId").GetString());
        Assert.Equal("conn-prev", root.GetProperty("connectionId").GetString());
        Assert.Equal("peer-prev", root.GetProperty("peerSuccess").GetString());
        Assert.Equal("ru", root.GetProperty("locale").GetString());
        Assert.Equal("Europe/Moscow", root.GetProperty("timeZone").GetString());
    }

    [Fact]
    public void Register_omits_connection_fields_on_first_session()
    {
        VoiceWireEvent wire = VoiceCodec.Register("user-7", null, null, new VoiceOptions());
        string json = wire.Root.GetRawText();
        Assert.DoesNotContain("connectionId", json);
        Assert.DoesNotContain("peerSuccess", json);
    }

    [Fact]
    public void ScanForPeer_without_token_omits_token_and_token_type()
    {
        VoiceWireEvent wire = VoiceCodec.ScanForPeer(
            new VoiceSearchCriteria(UserSex: "MALE", PeerAges: [new VoiceAgeRange(18, 21), new VoiceAgeRange(22, 26)]), captchaToken: null);

        JsonElement root = wire.Root;
        Assert.Equal("scan-for-peer", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("peerToPeer").GetBoolean());
        Assert.Equal(2, root.GetProperty("searchCriteria").GetProperty("peerAges").GetArrayLength());
        string json = root.GetRawText();
        Assert.DoesNotContain("token", json);
    }

    [Fact]
    public void ScanForPeer_defaults_matches_android_reference()
    {
        VoiceWireEvent wire = VoiceCodec.ScanForPeer(new VoiceSearchCriteria(), captchaToken: null);
        JsonElement criteria = wire.Root.GetProperty("searchCriteria");
        Assert.Equal(0, criteria.GetProperty("group").GetInt32());
        Assert.Equal("ANY", criteria.GetProperty("peerSex").GetString());
        Assert.Equal("ANY", criteria.GetProperty("userSex").GetString());
    }

    [Fact]
    public void ScanForPeer_with_token_adds_image_token_type()
    {
        VoiceWireEvent wire = VoiceCodec.ScanForPeer(new VoiceSearchCriteria(), "tok");
        Assert.Equal("tok", wire.Root.GetProperty("token").GetString());
        Assert.Equal("IMAGE", wire.Root.GetProperty("tokenType").GetString());
    }

    [Fact]
    public void Offer_wraps_sdp_in_typed_envelope_string()
    {
        VoiceWireEvent wire = VoiceCodec.Offer("p1", "offer", "v=0 offer-body");
        JsonElement root = wire.Root;
        Assert.Equal("p1", root.GetProperty("connectionId").GetString());
        using JsonDocument doc = JsonDocument.Parse(root.GetProperty("offer").GetString()!);
        Assert.Equal("offer", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("v=0 offer-body", doc.RootElement.GetProperty("sdp").GetString());
    }

    [Fact]
    public void Answer_wraps_sdp_in_typed_envelope_string()
    {
        VoiceWireEvent wire = VoiceCodec.Answer("p1", "answer", "v=0 answer-body");
        using JsonDocument doc = JsonDocument.Parse(wire.Root.GetProperty("answer").GetString()!);
        Assert.Equal("answer", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("v=0 answer-body", doc.RootElement.GetProperty("sdp").GetString());
    }

    [Fact]
    public void IceCandidate_embeds_candidate_object_as_json_string()
    {
        VoiceWireEvent wire = VoiceCodec.IceCandidate(
            "p1", new VoiceIceCandidate("0", 2, "candidate:1 1 udp"));

        JsonElement root = wire.Root;
        Assert.Equal("ice-candidate", root.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("candidate").ValueKind);
        using JsonDocument doc = JsonDocument.Parse(root.GetProperty("candidate").GetString()!);
        JsonElement inner = doc.RootElement.GetProperty("candidate");
        Assert.Equal("0", inner.GetProperty("sdpMid").GetString());
        Assert.Equal(2, inner.GetProperty("sdpMLineIndex").GetInt32());
        Assert.Equal("candidate:1 1 udp", inner.GetProperty("candidate").GetString());
    }

    [Fact]
    public void Connection_events_carry_only_type_and_id()
    {
        Assert.Equal(
            """{"type":"peer-disconnect","connectionId":"p9"}""",
            VoiceCodec.PeerDisconnect("p9").Root.GetRawText());
        Assert.Equal(
            """{"type":"stream-received","connectionId":"p9"}""",
            VoiceCodec.StreamReceived("p9").Root.GetRawText());
    }

    [Fact]
    public void UsersCountRequest_has_type_only()
    {
        VoiceWireEvent wire = VoiceCodec.UsersCountRequest();
        Assert.Equal("users-count-request", wire.Type);
        Assert.Equal("""{"type":"users-count-request"}""", wire.Root.GetRawText());
    }

    [Fact]
    public void Parse_extracts_type_discriminator()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"type":"users-count","usersCount":7}""");
        VoiceWireEvent wire = VoiceCodec.Parse(doc.RootElement);
        Assert.Equal("users-count", wire.Type);
    }

    [Fact]
    public void Parse_rejects_events_without_type()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"usersCount":7}""");
        Assert.Throws<JsonException>(() => VoiceCodec.Parse(doc.RootElement));
    }

    [Fact]
    public void ToCandidate_decodes_nested_envelope()
    {
        VoiceIceCandidate candidate = VoiceCodec.ToCandidate(
            """{"candidate":{"sdpMid":"1","sdpMLineIndex":0,"candidate":"candidate:x"}}""");
        Assert.Equal(("1", 0, "candidate:x"), (candidate.SdpMid, candidate.SdpMLineIndex, candidate.Candidate));
    }

    [Fact]
    public void FromSdpString_returns_type_and_body()
    {
        (string type, string sdp) = VoiceCodec.FromSdpString("""{"type":"offer","sdp":"v=0"}""");
        Assert.Equal(("offer", "v=0"), (type, sdp));
    }

    [Fact]
    public void PeerConnection_with_true_includes_connectionId()
    {
        VoiceWireEvent wire = VoiceCodec.PeerConnection(true, "conn-123");
        Assert.Equal("peer-connection", wire.Type);
        Assert.True(wire.Root.GetProperty("connection").GetBoolean());
        Assert.Equal("conn-123", wire.Root.GetProperty("connectionId").GetString());
    }

    [Fact]
    public void PeerConnection_with_false_omits_null_connectionId()
    {
        VoiceWireEvent wire = VoiceCodec.PeerConnection(false);
        Assert.Equal("peer-connection", wire.Type);
        Assert.False(wire.Root.GetProperty("connection").GetBoolean());
        Assert.False(wire.Root.TryGetProperty("connectionId", out _));
    }

    [Fact]
    public void ScanForPeer_serializes_complex_userAge_and_multiple_peerAges()
    {
        var criteria = new VoiceSearchCriteria(
            UserSex: "MALE",
            PeerSex: "FEMALE",
            PeerAges: [new VoiceAgeRange(18, 24), new VoiceAgeRange(25, 32)],
            UserAge: new VoiceAgeRange(25, 32),
            Group: 0);

        VoiceWireEvent wire = VoiceCodec.ScanForPeer(criteria, captchaToken: null);
        JsonElement root = wire.Root;

        Assert.Equal("scan-for-peer", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("peerToPeer").GetBoolean());

        JsonElement searchCriteria = root.GetProperty("searchCriteria");
        Assert.Equal(0, searchCriteria.GetProperty("group").GetInt32());
        Assert.Equal("MALE", searchCriteria.GetProperty("userSex").GetString());
        Assert.Equal("FEMALE", searchCriteria.GetProperty("peerSex").GetString());

        JsonElement userAge = searchCriteria.GetProperty("userAge");
        Assert.Equal(25, userAge.GetProperty("from").GetInt32());
        Assert.Equal(32, userAge.GetProperty("to").GetInt32());

        JsonElement peerAges = searchCriteria.GetProperty("peerAges");
        Assert.Equal(2, peerAges.GetArrayLength());
        Assert.Equal(18, peerAges[0].GetProperty("from").GetInt32());
        Assert.Equal(24, peerAges[0].GetProperty("to").GetInt32());
        Assert.Equal(25, peerAges[1].GetProperty("from").GetInt32());
        Assert.Equal(32, peerAges[1].GetProperty("to").GetInt32());
    }

    [Fact]
    public void RegisterWeb_matches_web_audiochat_format()
    {
        VoiceWireEvent wire = VoiceCodec.RegisterWeb("web-token-123", "ru", "Europe/Kyiv", isTouch: false);

        Assert.Equal("register", wire.Type);
        JsonElement root = wire.Root;
        Assert.False(root.GetProperty("android").GetBoolean());
        Assert.Equal(24, root.GetProperty("version").GetInt32());
        Assert.Equal("web-token-123", root.GetProperty("userId").GetString());
        Assert.False(root.GetProperty("isTouch").GetBoolean());
        Assert.True(root.GetProperty("messengerNeedAuth").GetBoolean());
        Assert.Equal("Europe/Kyiv", root.GetProperty("timeZone").GetString());
        Assert.Equal("ru", root.GetProperty("locale").GetString());
    }

    [Fact]
    public void WebAntibotHelper_generates_valid_proof_and_fingerprint()
    {
        using var challengeDoc = JsonDocument.Parse("{\"type\":\"challenge-request\",\"challengeId\":\"test-seed-1\",\"echo\":1234}");
        VoiceWireEvent proof = WebAntibotHelper.BuildChallengeProof(challengeDoc.RootElement);

        Assert.Equal("challenge-proof", proof.Type);
        Assert.Equal("test-seed-1", proof.Root.GetProperty("challengeId").GetString());
        Assert.Equal(24, proof.Root.GetProperty("clientVersion").GetInt32());
        Assert.Equal(1234, proof.Root.GetProperty("echo").GetInt32());
        Assert.NotEmpty(proof.Root.GetProperty("proof").GetString()!);
        Assert.NotEmpty(proof.Root.GetProperty("checksum").GetString()!);

        VoiceWireEvent fpt = WebAntibotHelper.BuildSetFpt("Mozilla/5.0", "tok123", "ru");
        Assert.Equal("set-fpt", fpt.Type);
        Assert.Equal(32, fpt.Root.GetProperty("fpt").GetString()!.Length);
        Assert.NotEmpty(fpt.Root.GetProperty("infoData").GetString()!);

        VoiceWireEvent webAgent = WebAntibotHelper.BuildWebAgent("test-uid-1", 59582917);
        Assert.Equal("web-agent", webAgent.Type);
        Assert.NotEmpty(webAgent.Root.GetProperty("data").GetString()!);
    }

    [Fact]
    public void ScanForPeer_with_recaptcha_token_type()
    {
        var wire = VoiceCodec.ScanForPeer(new VoiceSearchCriteria(), "dummy-recaptcha-token", "RECAPTCHA");
        Assert.Equal("RECAPTCHA", wire.Root.GetProperty("tokenType").GetString());
        Assert.Equal("dummy-recaptcha-token", wire.Root.GetProperty("token").GetString());
    }
}



