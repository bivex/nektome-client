using System.Text.Json;
using System.Text.Json.Serialization;
using NektoMe.Application.Abstractions;

namespace NektoMe.Application.Protocol;

/// <summary>
/// Maps voice-session commands and server payloads to the flat
/// <c>{"type": …}</c> wire format. Single source of truth for JSON shapes.
/// </summary>
public static class VoiceCodec
{
    public static readonly JsonSerializerOptions Wire = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // -- outbound -------------------------------------------------------------

    public static VoiceWireEvent Register(
        string userId,
        string? lastConnectionId,
        string? peerSuccess,
        VoiceOptions options)
    {
        string timeZone = options.TimeZone ?? TimeZoneInfo.Local.Id;
        return new VoiceWireEvent(
            VoiceWireNames.Register,
            Serialize(new VoiceRegisterPayload(
                Android: true,
                ConnectionId: lastConnectionId,
                Locale: options.Locale,
                PeerSuccess: peerSuccess,
                TimeZone: timeZone,
                UserId: userId,
                Type: VoiceWireNames.Register)));
    }

    public static VoiceWireEvent RegisterWeb(
        string token,
        string locale,
        string timeZone,
        bool isTouch = false)
    {
        return new VoiceWireEvent(
            VoiceWireNames.Register,
            Serialize(new VoiceWebRegisterPayload(
                Type: VoiceWireNames.Register,
                Android: false,
                Version: WebAntibotHelper.ClientVersion,
                UserId: token,
                IsTouch: isTouch,
                MessengerNeedAuth: true,
                TimeZone: timeZone,
                Locale: locale)));
    }

    public static VoiceWireEvent ScanForPeer(VoiceSearchCriteria criteria, string? captchaToken, string? tokenType = null)
    {
        var payload = new VoiceSearchCriteriaPayload(
            Group: criteria.Group,
            PeerAges: criteria.PeerAges,
            PeerSex: criteria.PeerSex,
            UserAge: criteria.UserAge,
            UserSex: criteria.UserSex);

        string? resolvedTokenType = captchaToken is null ? null : (tokenType ?? "IMAGE");

        return new VoiceWireEvent(
            VoiceWireNames.ScanForPeer,
            Serialize(new VoiceScanForPeerPayload(
                PeerToPeer: true,
                SearchCriteria: payload,
                Token: captchaToken,
                TokenType: resolvedTokenType,
                Type: VoiceWireNames.ScanForPeer)));
    }

    public static VoiceWireEvent StopScan() =>
        new(VoiceWireNames.StopScan, Serialize(new VoiceTypeOnlyPayload(VoiceWireNames.StopScan)));

    public static VoiceWireEvent Offer(string connectionId, string type, string sdp) =>
        new(VoiceWireNames.Offer, Serialize(new VoiceOfferPayload(
            VoiceWireNames.Offer, connectionId, SdpString(type, sdp))));

    public static VoiceWireEvent Answer(string connectionId, string type, string sdp) =>
        new(VoiceWireNames.Answer, Serialize(new VoiceAnswerPayload(
            VoiceWireNames.Answer, connectionId, SdpString(type, sdp))));

    public static VoiceWireEvent IceCandidate(string connectionId, VoiceIceCandidate candidate) =>
        new(VoiceWireNames.IceCandidate, Serialize(new VoiceConnectionIdWirePayload(
            VoiceWireNames.IceCandidate,
            connectionId,
            SerializeToString(new IceCandidateDescription(new VoiceCandidateData(
                candidate.SdpMid,
                candidate.SdpMLineIndex,
                candidate.Candidate))))));

    public static VoiceWireEvent PeerDisconnect(string connectionId) =>
        ConnectionEvent(VoiceWireNames.PeerDisconnect, connectionId);

    public static VoiceWireEvent PeerSoftDisconnect(string connectionId) =>
        ConnectionEvent(VoiceWireNames.PeerSoftDisconnect, connectionId);

    public static VoiceWireEvent PeerTrouble(string connectionId) =>
        ConnectionEvent(VoiceWireNames.PeerTrouble, connectionId);

    public static VoiceWireEvent StreamReceived(string connectionId) =>
        ConnectionEvent(VoiceWireNames.StreamReceived, connectionId);

    public static VoiceWireEvent PeerMute(string connectionId, bool muted) =>
        new(VoiceWireNames.PeerMute, Serialize(new VoicePeerMutePayload(
            VoiceWireNames.PeerMute, connectionId, muted)));

    public static VoiceWireEvent PeerConnection(bool connection, string? connectionId = null) =>
        new(VoiceWireNames.PeerConnection, Serialize(new VoicePeerConnectionPayload(
            VoiceWireNames.PeerConnection, connection, connectionId)));

    public static VoiceWireEvent UsersCountRequest() =>
        new(VoiceWireNames.UsersCountRequest, Serialize(new VoiceTypeOnlyPayload(VoiceWireNames.UsersCountRequest)));

    public static VoiceWireEvent LogPingResults(IReadOnlyList<VoicePingResultData> results) =>
        new(VoiceWireNames.LogPingResults,
            Serialize(new VoiceLogPingResultsPayload(VoiceWireNames.LogPingResults, results)));

    // -- inbound --------------------------------------------------------------

    public static VoiceWireEvent Parse(JsonElement root)
    {
        string? type = root.ValueKind == JsonValueKind.Object
            ? root.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null
            : null;

        return type is null
            ? throw new JsonException("Voice event has no 'type' discriminator.")
            : new VoiceWireEvent(type, root.Clone());
    }

    public static T Parse<T>(VoiceWireEvent e) where T : class =>
        e.Root.Deserialize<T>(Wire)
        ?? throw new JsonException($"Voice event '{e.Type}' could not be parsed as {typeof(T).Name}.");

    /// <summary>Decodes a candidate string (<c>{"candidate":{…}}</c>, flat JSON, or raw candidate) into a transport-neutral candidate.</summary>
    public static VoiceIceCandidate ToCandidate(string candidateJson)
    {
        if (string.IsNullOrWhiteSpace(candidateJson))
        {
            throw new JsonException("Candidate string is empty.");
        }

        string trimmed = candidateJson.Trim();
        if (!trimmed.StartsWith("{"))
        {
            return new VoiceIceCandidate("0", 0, trimmed);
        }

        using var doc = JsonDocument.Parse(trimmed);
        JsonElement target = doc.RootElement;
        if (target.TryGetProperty("candidate", out JsonElement inner) && inner.ValueKind == JsonValueKind.Object)
        {
            target = inner;
        }

        string sdpMid = target.TryGetProperty("sdpMid", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "0" : "0";
        int sdpMLine = 0;
        if (target.TryGetProperty("sdpMLineIndex", out var ml) && ml.ValueKind == JsonValueKind.Number)
        {
            ml.TryGetInt32(out sdpMLine);
        }

        string cand = string.Empty;
        if (target.TryGetProperty("candidate", out var c))
        {
            cand = c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : c.GetRawText();
        }

        return new VoiceIceCandidate(sdpMid, sdpMLine, cand);
    }

    /// <summary>Decodes an SDP string (<c>{"type","sdp"}</c> or raw SDP) into its parts.</summary>
    public static (string Type, string Sdp) FromSdpString(string sdpJson)
    {
        string trimmed = sdpJson.Trim();
        if (trimmed.StartsWith("{"))
        {
            using var doc = JsonDocument.Parse(trimmed);
            string type = doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "offer"
                : "offer";
            string sdp = doc.RootElement.TryGetProperty("sdp", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? string.Empty
                : string.Empty;
            return (type, sdp);
        }

        return ("offer", trimmed);
    }

    /// <summary>
    /// Robustly extracts SDP description and connectionId from an incoming <c>offer</c> or <c>answer</c> wire event.
    /// Supports both web browsers (SDP wrapped as a JSON string) and Android/custom peers (SDP as a JSON object).
    /// </summary>
    public static (string Type, string Sdp, string ConnectionId) ExtractSdp(VoiceWireEvent e)
    {
        string connectionId = e.Root.TryGetProperty("connectionId", out JsonElement connProp) && connProp.ValueKind == JsonValueKind.String
            ? connProp.GetString() ?? string.Empty
            : string.Empty;

        string propName = e.Type == VoiceWireNames.Offer ? "offer" : "answer";
        if (!e.Root.TryGetProperty(propName, out JsonElement sdpElem))
        {
            if (!e.Root.TryGetProperty("sdp", out sdpElem) &&
                !e.Root.TryGetProperty(e.Type == VoiceWireNames.Offer ? "answer" : "offer", out sdpElem))
            {
                throw new JsonException($"Voice event '{e.Type}' carries no SDP payload.");
            }
        }

        if (sdpElem.ValueKind == JsonValueKind.String)
        {
            (string t, string s) = FromSdpString(sdpElem.GetString() ?? string.Empty);
            return (t, s, connectionId);
        }
        else if (sdpElem.ValueKind == JsonValueKind.Object)
        {
            string type = sdpElem.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? e.Type
                : e.Type;
            string sdp = sdpElem.TryGetProperty("sdp", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? string.Empty
                : string.Empty;
            return (type, sdp, connectionId);
        }

        throw new JsonException($"Unexpected SDP element kind '{sdpElem.ValueKind}' in voice event '{e.Type}'.");
    }

    /// <summary>
    /// Robustly extracts an ICE candidate and connectionId from an incoming <c>ice-candidate</c> wire event.
    /// Supports both web browsers (candidate wrapped as a JSON string) and Android/custom peers (candidate as a JSON object).
    /// Returns null if candidate gathering is complete (null candidate).
    /// </summary>
    public static (VoiceIceCandidate? Candidate, string ConnectionId) ExtractCandidate(VoiceWireEvent e)
    {
        string connectionId = e.Root.TryGetProperty("connectionId", out JsonElement connProp) && connProp.ValueKind == JsonValueKind.String
            ? connProp.GetString() ?? string.Empty
            : string.Empty;

        if (!e.Root.TryGetProperty("candidate", out JsonElement candElem) ||
            candElem.ValueKind == JsonValueKind.Null ||
            candElem.ValueKind == JsonValueKind.Undefined)
        {
            return (null, connectionId);
        }

        if (candElem.ValueKind == JsonValueKind.String)
        {
            string raw = candElem.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return (null, connectionId);
            return (ToCandidate(raw), connectionId);
        }
        else if (candElem.ValueKind == JsonValueKind.Object)
        {
            return (ToCandidate(candElem.GetRawText()), connectionId);
        }

        return (null, connectionId);
    }

    // -- helpers --------------------------------------------------------------

    private static VoiceWireEvent ConnectionEvent(string type, string connectionId) =>
        new(type, Serialize(new VoiceConnectionIdPayload(type, connectionId)));

    private static string SdpString(string type, string sdp) =>
        SerializeToString(new SdpDescription(type, sdp));

    private static T ParseFromString<T>(string json) where T : class
    {
        var value = JsonSerializer.Deserialize<T>(json, Wire);
        return value ?? throw new JsonException($"Voice wire string could not be parsed as {typeof(T).Name}.");
    }

    private static string SerializeToString(object payload) => JsonSerializer.Serialize(payload, Wire);

    private static JsonElement Serialize(object payload) => JsonSerializer.SerializeToElement(payload, Wire);

    /// <summary>Outbound ice-candidate payload (candidate embedded as a JSON string).</summary>
    private sealed record VoiceConnectionIdWirePayload(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("connectionId")] string ConnectionId,
        [property: JsonPropertyName("candidate")] string Candidate);
}
