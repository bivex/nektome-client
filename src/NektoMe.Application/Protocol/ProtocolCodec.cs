using System.Text.Json;
using System.Text.Json.Serialization;
using NektoMe.Application.Abstractions;
using NektoMe.Domain;

namespace NektoMe.Application.Protocol;

/// <summary>Search preferences expressed in application terms.</summary>
public sealed record SearchCriteria(
    Sex? MySex = null,
    Sex? WishSex = null,
    int[]? MyAge = null,
    IReadOnlyList<(int From, int To)>? WishAge = null,
    bool IncludeAdult = false,
    bool DisableVoice = false,
    bool RolePlay = false);

/// <summary>
/// Maps application-level commands and server payloads to the wire format.
/// This is the single place that knows the JSON shapes.
/// </summary>
public static class ProtocolCodec
{
    public static readonly JsonSerializerOptions Wire = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // -- outbound ------------------------------------------------------------

    public static OutboundMessage AuthSendToken(string token, string key, ChatOptions options) =>
        new(Serialize(new AuthSendTokenPayload(
            WireNames.AuthSendToken,
            token,
            key,
            options.SigKey,
            ResolveTimeZone(options),
            options.Locale,
            InsKey: null,
            options.Vending,
            options.PushToken,
            options.PushType)));

    public static OutboundMessage AuthGetToken(DeviceIdentity device, string key, ChatOptions options) =>
        new(Serialize(new AuthGetTokenPayload(
            WireNames.AuthGetToken,
            device.DeviceId,
            device.DeviceName,
            device.DeviceType,
            key,
            options.SigKey,
            ResolveTimeZone(options),
            options.Locale,
            InsKey: null,
            options.Vending,
            options.PushToken,
            options.PushType)));

    public static OutboundMessage SearchRun(SearchCriteria criteria)
    {
        int[][]? wishAge = criteria.WishAge is null
            ? null
            : criteria.WishAge.Select(range => new[] { range.From, range.To }).ToArray();

        return new OutboundMessage(Serialize(new SearchRunPayload(
            WireNames.SearchRun,
            criteria.IncludeAdult ? true : null,
            criteria.RolePlay,
            criteria.DisableVoice ? true : null,
            criteria.MyAge,
            ToWireSex(criteria.MySex),
            wishAge,
            ToWireSex(criteria.WishSex))));
    }

    public static OutboundMessage SearchOut() => new(SerializeAction(WireNames.SearchOut));

    public static OutboundMessage DialogInfo(long dialogId) =>
        new(Serialize(new DialogIdPayload(WireNames.DialogInfo, dialogId)));

    public static OutboundMessage AnonMessage(long dialogId, string text, long randomId) =>
        new(Serialize(new AnonMessagePayload(WireNames.AnonMessage, dialogId, text, randomId)));

    public static OutboundMessage DialogTyping(long dialogId, bool typing, bool voice = false) =>
        new(Serialize(new TypingPayload(WireNames.DialogTyping, dialogId, typing, voice)));

    public static OutboundMessage AnonLeaveDialog(long dialogId) =>
        new(Serialize(new DialogIdPayload(WireNames.AnonLeaveDialog, dialogId)));

    public static OutboundMessage AnonReadMessages(long dialogId, long lastMessageId) =>
        new(Serialize(new ReadMessagesPayload(WireNames.AnonReadMessages, dialogId, lastMessageId)));

    public static OutboundMessage OnlineTrack(bool on) =>
        new(Serialize(new OnlineTrackPayload(WireNames.OnlineTrack, on)));

    public static OutboundMessage CaptchaVerify(string solution) =>
        new(Serialize(new CaptchaPayload(WireNames.CaptchaVerify, solution)));

    public static OutboundMessage ReportDialog(long dialogId, long messageId, int reasonId) =>
        new(Serialize(new ReportPayload(WireNames.ReportDialog, dialogId, messageId, reasonId)));

    // -- inbound -------------------------------------------------------------

    public static NoticeEnvelope ParseEnvelope(JsonElement root) =>
        root.Deserialize<NoticeEnvelope>(Wire)
        ?? throw new JsonException("Notice envelope is null.");

    public static T Parse<T>(JsonElement data) where T : class =>
        data.Deserialize<T>(Wire)
        ?? throw new JsonException($"Payload could not be parsed as {typeof(T).Name}.");

    /// <summary>
    /// Server timestamps arrive as unix time; values above 10^12 are milliseconds,
    /// smaller values are seconds.
    /// </summary>
    public static DateTimeOffset FromUnix(long? value)
    {
        if (value is null or 0)
        {
            return DateTimeOffset.MinValue;
        }

        return value.Value >= 1_000_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value)
            : DateTimeOffset.FromUnixTimeSeconds(value.Value);
    }

    // -- helpers -------------------------------------------------------------

    public static string? ToWireSex(Sex? sex) => sex switch
    {
        Sex.Female => "F",
        Sex.Male => "M",
        _ => null,
    };

    public static Sex? FromWireSex(string? value) => value switch
    {
        "F" => Sex.Female,
        "M" => Sex.Male,
        _ => null,
    };

    private static JsonElement Serialize(object payload) =>
        JsonSerializer.SerializeToElement(payload, Wire);

    private static JsonElement SerializeAction(string action) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["action"] = action }, Wire);

    private static string ResolveTimeZone(ChatOptions options) =>
        options.TimeZone ?? TimeZoneInfo.Local.Id;
}
