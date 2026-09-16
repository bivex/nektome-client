using System.Text.Json;
using System.Text.Json.Serialization;

namespace NektoMe.Application.Protocol;

// ---------------------------------------------------------------------------
// Voice wire models. Field names match the reference voice app's Gson
// annotations exactly; absent data is never serialized (nulls omitted).
// ---------------------------------------------------------------------------

/// <summary>Age range with from and to boundaries, matching reference AgeCriteria.</summary>
public sealed record VoiceAgeRange(
    [property: JsonPropertyName("from")] int From,
    [property: JsonPropertyName("to")] int To);

/// <summary>Search preferences for <c>scan-for-peer</c>, application terms.</summary>
public sealed record VoiceSearchCriteria(
    string UserSex = "ANY",
    string PeerSex = "ANY",
    IReadOnlyList<VoiceAgeRange>? PeerAges = null,
    VoiceAgeRange? UserAge = null,
    int Group = 0);

// -- outbound payloads -------------------------------------------------------

public sealed record VoiceRegisterPayload(
    [property: JsonPropertyName("android")] bool Android,
    [property: JsonPropertyName("connectionId")] string? ConnectionId,
    [property: JsonPropertyName("locale")] string? Locale,
    [property: JsonPropertyName("peerSuccess")] string? PeerSuccess,
    [property: JsonPropertyName("timeZone")] string? TimeZone,
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("type")] string Type);

public sealed record VoiceWebRegisterPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("android")] bool Android,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("isTouch")] bool IsTouch,
    [property: JsonPropertyName("messengerNeedAuth")] bool MessengerNeedAuth,
    [property: JsonPropertyName("timeZone")] string TimeZone,
    [property: JsonPropertyName("locale")] string Locale);

public sealed record VoiceSearchCriteriaPayload(
    [property: JsonPropertyName("group")] int Group,
    [property: JsonPropertyName("peerAges")] IReadOnlyList<VoiceAgeRange>? PeerAges = null,
    [property: JsonPropertyName("peerSex")] string PeerSex = "ANY",
    [property: JsonPropertyName("userAge")] VoiceAgeRange? UserAge = null,
    [property: JsonPropertyName("userSex")] string UserSex = "ANY");

public sealed record VoiceScanForPeerPayload(
    [property: JsonPropertyName("peerToPeer")] bool PeerToPeer,
    [property: JsonPropertyName("searchCriteria")] VoiceSearchCriteriaPayload SearchCriteria,
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("tokenType")] string? TokenType,
    [property: JsonPropertyName("type")] string Type);

public sealed record VoiceConnectionIdPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("connectionId")] string ConnectionId);

/// <summary><c>offer</c> event: the SDP travels in the <c>offer</c> field.</summary>
public sealed record VoiceOfferPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("connectionId")] string ConnectionId,
    [property: JsonPropertyName("offer")] string Offer);

/// <summary><c>answer</c> event: the SDP travels in the <c>answer</c> field.</summary>
public sealed record VoiceAnswerPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("connectionId")] string ConnectionId,
    [property: JsonPropertyName("answer")] string Answer);

public sealed record VoicePeerMutePayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("connectionId")] string ConnectionId,
    [property: JsonPropertyName("muted")] bool Muted);

public sealed record VoicePeerConnectionPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("connection")] bool Connection,
    [property: JsonPropertyName("connectionId")] string? ConnectionId = null);

public sealed record VoiceTypeOnlyPayload(
    [property: JsonPropertyName("type")] string Type);

// -- inbound payloads --------------------------------------------------------

public sealed record VoiceRegisteredData(
    [property: JsonPropertyName("success")] bool? Success,
    [property: JsonPropertyName("connectionId")] string? ConnectionId,
    [property: JsonPropertyName("errorCode")] int? ErrorCode,
    [property: JsonPropertyName("internal_id")] long? InternalId,
    [property: JsonPropertyName("premium")] bool? Premium,
    [property: JsonPropertyName("recaptchaSiteKey")] string? RecaptchaSiteKey,
    [property: JsonPropertyName("config")] JsonElement? Config);

/// <summary>Inbound <c>ping-server-request</c>: hosts to measure and probe count.</summary>
public sealed record VoicePingServerRequestData(
    [property: JsonPropertyName("attempts")] int? Attempts,
    [property: JsonPropertyName("list")] List<string>? List);

/// <summary>One entry of the <c>log-ping-results</c> reply.</summary>
public sealed record VoicePingResultData(
    [property: JsonPropertyName("server")] string? Server,
    [property: JsonPropertyName("ping")] int? Ping,
    [property: JsonPropertyName("fails")] int? Fails);

/// <summary>Outbound <c>log-ping-results</c> carrying the measured samples.</summary>
public sealed record VoiceLogPingResultsPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("result")] IReadOnlyList<VoicePingResultData> Result);

public sealed record VoicePeerConnectData(
    [property: JsonPropertyName("connectionId")] string? ConnectionId,
    [property: JsonPropertyName("initiator")] bool? Initiator,
    [property: JsonPropertyName("time")] long? Time,
    [property: JsonPropertyName("relay")] bool? Relay,
    [property: JsonPropertyName("stunUrl")] string? StunUrl,
    [property: JsonPropertyName("turnParams"), JsonConverter(typeof(TurnParamsJsonConverter))] List<VoiceTurnParamData>? TurnParams);

public sealed class TurnParamsJsonConverter : JsonConverter<List<VoiceTurnParamData>?>
{
    public override List<VoiceTurnParamData>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            string? raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string trimmed = raw.Trim();
            if (trimmed.StartsWith("["))
            {
                return JsonSerializer.Deserialize<List<VoiceTurnParamData>>(trimmed, options);
            }

            if (trimmed.StartsWith("{"))
            {
                VoiceTurnParamData? single = JsonSerializer.Deserialize<VoiceTurnParamData>(trimmed, options);
                return single is null ? null : [single];
            }

            return null;
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            return JsonSerializer.Deserialize<List<VoiceTurnParamData>>(doc.RootElement.GetRawText(), options);
        }

        return null;
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<VoiceTurnParamData>? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }
}

public sealed record VoiceTurnParamData(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("credential")] string? Credential);

/// <summary>Inbound SDP events carry their description in the type-named field.</summary>
public sealed record VoiceSdpData(
    [property: JsonPropertyName("connectionId")] string? ConnectionId,
    [property: JsonPropertyName("offer")] string? Offer,
    [property: JsonPropertyName("answer")] string? Answer);

public sealed record VoiceIceCandidateData(
    [property: JsonPropertyName("connectionId")] string? ConnectionId,
    [property: JsonPropertyName("candidate")] string? Candidate);

/// <summary>The inner JSON carried inside a candidate string: <c>{"candidate":{…}}</c>.</summary>
public sealed record VoiceCandidateEnvelope(
    [property: JsonPropertyName("candidate")] VoiceCandidateData? Candidate);

public sealed record VoiceCandidateData(
    [property: JsonPropertyName("sdpMid")] string? SdpMid,
    [property: JsonPropertyName("sdpMLineIndex")] int? SdpMLineIndex,
    [property: JsonPropertyName("candidate")] string? Candidate);

public sealed record VoicePeerMuteData(
    [property: JsonPropertyName("connectionId")] string? ConnectionId,
    [property: JsonPropertyName("muted")] bool? Muted);

public sealed record VoiceConnectionData(
    [property: JsonPropertyName("connectionId")] string? ConnectionId);

public sealed record VoiceUsersCountData(
    [property: JsonPropertyName("usersCount")] int? UsersCount,
    [property: JsonPropertyName("waitingUsersCount")] int? WaitingUsersCount,
    [property: JsonPropertyName("talkingUsersCount")] int? TalkingUsersCount);

public sealed record VoiceCaptchaRequestData(
    [property: JsonPropertyName("captchaType")] string? CaptchaType,
    [property: JsonPropertyName("leftChats")] int? LeftChats,
    [property: JsonPropertyName("needChats")] int? NeedChats,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("report")] string? Report);

/// <summary>Loose shape for the server's <c>error</c> event (fields vary).</summary>
public sealed record VoiceErrorData(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("description")] string? Description);

// -- SDP and candidate string envelopes --------------------------------------

/// <summary>The SDP description string: <c>{"type":"offer","sdp":"…"}</c>.</summary>
public sealed record SdpDescription(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("sdp")] string Sdp);

/// <summary>The candidate string: <c>{"candidate":{"sdpMid",…}}</c>.</summary>
public sealed record IceCandidateDescription(
    [property: JsonPropertyName("candidate")] VoiceCandidateData Candidate);
