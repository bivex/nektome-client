using System.Text.Json;
using System.Text.Json.Serialization;

namespace NektoMe.Application.Protocol;

// ---------------------------------------------------------------------------
// Inbound envelopes and payloads. Field names match the reference client's
// JSON annotations exactly (camelCase; nulls never appear for absent data).
// ---------------------------------------------------------------------------

public sealed record NoticeEnvelope(
    [property: JsonPropertyName("notice")] string? Notice,
    [property: JsonPropertyName("data")] JsonElement? Data,
    [property: JsonPropertyName("badge")] int? Badge,
    [property: JsonPropertyName("page")] int? Page);

public sealed record AuthTokenData(
    [property: JsonPropertyName("config")] AuthConfigData? Config,
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("statusInfo")] StatusInfoData? StatusInfo,
    [property: JsonPropertyName("tokenInfo")] TokenInfoData? TokenInfo);

public sealed record TokenInfoData(
    [property: JsonPropertyName("authToken")] string? AuthToken,
    [property: JsonPropertyName("paidType")] int? PaidType,
    [property: JsonPropertyName("pushToken")] string? PushToken);

public sealed record StatusInfoData(
    [property: JsonPropertyName("anonDialogId")] long? AnonDialogId,
    [property: JsonPropertyName("communicationBan")] bool? CommunicationBan);

public sealed record AuthConfigData(
    [property: JsonPropertyName("alertInfo")] string? AlertInfo,
    [property: JsonPropertyName("banTalk")] bool? BanTalk,
    [property: JsonPropertyName("rulesEnable")] bool? RulesEnable,
    [property: JsonPropertyName("ages")] AgesConfigData? Ages,
    [property: JsonPropertyName("audioChat")] bool? AudioChat,
    [property: JsonPropertyName("reportReasons")] List<string>? ReportReasons,
    [property: JsonPropertyName("voiceConfig")] VoiceConfigData? VoiceConfig,
    [property: JsonPropertyName("rulesAdult")] List<AgeGroupData>? RulesAdult,
    [property: JsonPropertyName("rulesRole")] List<AgeGroupData>? RulesRole,
    [property: JsonPropertyName("rulesTalk")] List<AgeGroupData>? RulesTalk,
    [property: JsonPropertyName("subsDisabled")] bool? SubsDisabled);

public sealed record AgesConfigData(
    [property: JsonPropertyName("adult")] List<AgeGroupData>? Adult,
    [property: JsonPropertyName("communication")] List<AgeGroupData>? Communication,
    [property: JsonPropertyName("role")] List<AgeGroupData>? Role);

public sealed record AgeGroupData(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("to")] int? To,
    [property: JsonPropertyName("selected")] bool? Selected);

public sealed record VoiceConfigData(
    [property: JsonPropertyName("availableSeconds")] int? AvailableSeconds,
    [property: JsonPropertyName("beta")] bool? Beta,
    [property: JsonPropertyName("maxSeconds")] int? MaxSeconds,
    [property: JsonPropertyName("minSeconds")] int? MinSeconds);

public sealed record DialogData(
    [property: JsonPropertyName("close")] bool? Close,
    [property: JsonPropertyName("createTime")] long? CreateTime,
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("interlocutors")] List<long>? Interlocutors,
    [property: JsonPropertyName("messages")] List<MessageData>? Messages,
    [property: JsonPropertyName("supportVoice")] bool? SupportVoice,
    [property: JsonPropertyName("updateTime")] long? UpdateTime);

public sealed record MessageData(
    [property: JsonPropertyName("createTime")] long? CreateTime,
    [property: JsonPropertyName("dialogId")] long? DialogId,
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("message")] string? Text,
    [property: JsonPropertyName("randomId")] long? RandomId,
    [property: JsonPropertyName("read")] bool? Read,
    [property: JsonPropertyName("senderId")] long? SenderId);

public sealed record TypingData(
    [property: JsonPropertyName("dialogId")] long? DialogId,
    [property: JsonPropertyName("typing")] bool? Typing,
    [property: JsonPropertyName("voice")] bool? Voice);

public sealed record MessagesReadData(
    [property: JsonPropertyName("dialogId")] long? DialogId,
    [property: JsonPropertyName("reads")] List<long>? Reads);

public sealed record OnlineCountData(
    [property: JsonPropertyName("inChats")] int? InChats,
    [property: JsonPropertyName("inSearch")] int? InSearch);

public sealed record ErrorData(
    [property: JsonPropertyName("additional")] Dictionary<string, JsonElement>? Additional,
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("description")] string? Description);

public sealed record CaptchaData(
    [property: JsonPropertyName("solution")] string? Solution);

public sealed record DialogPaidData(
    [property: JsonPropertyName("dialogId")] long? DialogId,
    [property: JsonPropertyName("paid")] bool? Paid);

public sealed record PurchaseData(
    [property: JsonPropertyName("paidType")] int? PaidType);

// ---------------------------------------------------------------------------
// Outbound payloads.
// ---------------------------------------------------------------------------

public sealed record AuthSendTokenPayload(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("sigKey")] string? SigKey,
    [property: JsonPropertyName("timeZone")] string? TimeZone,
    [property: JsonPropertyName("locale")] string? Locale,
    [property: JsonPropertyName("insKey")] string? InsKey,
    [property: JsonPropertyName("vending")] string? Vending,
    [property: JsonPropertyName("pushToken")] string? PushToken,
    [property: JsonPropertyName("pType")] int? PushType);

public sealed record AuthGetTokenPayload(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("deviceType")] int DeviceType,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("sigKey")] string? SigKey,
    [property: JsonPropertyName("timeZone")] string? TimeZone,
    [property: JsonPropertyName("locale")] string? Locale,
    [property: JsonPropertyName("insKey")] string? InsKey,
    [property: JsonPropertyName("vending")] string? Vending,
    [property: JsonPropertyName("push")] string? Push,
    [property: JsonPropertyName("pType")] int? PushType);

public sealed record SearchRunPayload(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("isAdult")] bool? IsAdult,
    [property: JsonPropertyName("isRole")] bool IsRole,
    [property: JsonPropertyName("isVoiceDisable")] bool? IsVoiceDisable,
    [property: JsonPropertyName("myAge")] int[]? MyAge,
    [property: JsonPropertyName("mySex")] string? MySex,
    [property: JsonPropertyName("wishAge")] int[][]? WishAge,
    [property: JsonPropertyName("wishSex")] string? WishSex);

public sealed record AnonMessagePayload(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("dialogId")] long DialogId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("randomId")] long RandomId);

public sealed record TypingPayload(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("dialogId")] long DialogId,
    [property: JsonPropertyName("typing")] bool Typing,
    [property: JsonPropertyName("voice")] bool Voice);

public sealed record DialogIdPayload(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("dialogId")] long DialogId);

public sealed record ReadMessagesPayload(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("dialogId")] long DialogId,
    [property: JsonPropertyName("lastMessageId")] long LastMessageId);

public sealed record OnlineTrackPayload(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("on")] bool On);

public sealed record CaptchaPayload(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("solution")] string Solution);

public sealed record ReportPayload(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("dialogId")] long DialogId,
    [property: JsonPropertyName("messageId")] long MessageId,
    [property: JsonPropertyName("reasonId")] int ReasonId);
