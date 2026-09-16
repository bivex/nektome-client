using System.Text.Json;
using NektoMe.Application.Protocol;
using NektoMe.Application.Abstractions;
using NektoMe.Domain;

namespace NektoMe.Application.Tests;

public class ProtocolCodecTests
{
    [Fact]
    public void AuthGetToken_PayloadShape()
    {
        var device = new DeviceIdentity("device-1", "Mac-mini", 1);
        var options = new ChatOptions { Locale = "en" };

        var json = JsonDocument.Parse(ProtocolCodec.AuthGetToken(device, "KEY", options).Payload.GetRawText()).RootElement;

        Assert.Equal("auth.getToken", json.GetProperty("action").GetString());
        Assert.Equal("device-1", json.GetProperty("deviceId").GetString());
        Assert.Equal("Mac-mini", json.GetProperty("deviceName").GetString());
        Assert.Equal(1, json.GetProperty("deviceType").GetInt32());
        Assert.Equal("KEY", json.GetProperty("key").GetString());
        Assert.Equal("en", json.GetProperty("locale").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("timeZone").GetString()));
        Assert.False(json.TryGetProperty("insKey", out _));
        Assert.False(json.TryGetProperty("vending", out _));
        Assert.False(json.TryGetProperty("push", out _));
        Assert.False(json.TryGetProperty("pushToken", out _));
    }

    [Fact]
    public void AuthSendToken_IncludesToken()
    {
        var json = JsonDocument.Parse(
            ProtocolCodec.AuthSendToken("tok-1", "KEY", new ChatOptions()).Payload.GetRawText()).RootElement;

        Assert.Equal("auth.sendToken", json.GetProperty("action").GetString());
        Assert.Equal("tok-1", json.GetProperty("token").GetString());
        Assert.False(json.TryGetProperty("deviceId", out _));
    }

    [Fact]
    public void SearchRun_ShapesAgesAndSex()
    {
        var criteria = new SearchCriteria(
            MySex: Sex.Male,
            WishSex: Sex.Female,
            MyAge: [18, 21],
            WishAge: [(18, 21), (22, 25)],
            IncludeAdult: true,
            DisableVoice: true);

        var json = JsonDocument.Parse(ProtocolCodec.SearchRun(criteria).Payload.GetRawText()).RootElement;

        Assert.Equal("search.run", json.GetProperty("action").GetString());
        Assert.Equal("M", json.GetProperty("mySex").GetString());
        Assert.Equal("F", json.GetProperty("wishSex").GetString());
        Assert.Equal([18, 21], json.GetProperty("myAge").EnumerateArray().Select(v => v.GetInt32()).ToArray());
        Assert.Equal([[18, 21], [22, 25]], json.GetProperty("wishAge").EnumerateArray()
            .Select(r => r.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray());
        Assert.True(json.GetProperty("isAdult").GetBoolean());
        Assert.True(json.GetProperty("isVoiceDisable").GetBoolean());
        Assert.False(json.GetProperty("isRole").GetBoolean());
    }

    [Fact]
    public void SearchOut_IsBareAction()
    {
        Assert.Equal("""{"action":"search.sendOut"}""", ProtocolCodec.SearchOut().Payload.GetRawText());
    }

    [Fact]
    public void FromUnix_HandlesSecondsAndMilliseconds()
    {
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(123), ProtocolCodec.FromUnix(123));
        // Realistic epoch values: seconds are ~1e9, milliseconds ~1e12.
        Assert.Equal(
            DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000),
            ProtocolCodec.FromUnix(1_700_000_000_000));
        Assert.Equal(DateTimeOffset.MinValue, ProtocolCodec.FromUnix(null));
        Assert.Equal(DateTimeOffset.MinValue, ProtocolCodec.FromUnix(0));
    }

    [Fact]
    public void Sex_ConvertsBothWays()
    {
        Assert.Equal("F", ProtocolCodec.ToWireSex(Sex.Female));
        Assert.Equal("M", ProtocolCodec.ToWireSex(Sex.Male));
        Assert.Null(ProtocolCodec.ToWireSex(null));
        Assert.Equal(Sex.Female, ProtocolCodec.FromWireSex("F"));
        Assert.Null(ProtocolCodec.FromWireSex("X"));
    }

    [Fact]
    public void ParseEnvelope_ReadsAllFields()
    {
        var root = JsonDocument.Parse(
            """{"notice":"messages.new","data":{"id":1},"badge":2,"page":3}""").RootElement;

        var envelope = ProtocolCodec.ParseEnvelope(root);

        Assert.Equal("messages.new", envelope.Notice);
        Assert.Equal(2, envelope.Badge);
        Assert.Equal(3, envelope.Page);
        Assert.Equal(1, envelope.Data!.Value.GetProperty("id").GetInt32());
    }
}
