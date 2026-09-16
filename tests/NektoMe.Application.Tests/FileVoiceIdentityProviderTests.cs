using System.Globalization;
using System.Text.Json;
using NektoMe.Application;
using NektoMe.Application.Events;
using NektoMe.Application.Services;
using NektoMe.Infrastructure.Persistence;
using SocketIOClient.Common;

namespace NektoMe.Application.Tests;

public sealed class FileVoiceIdentityProviderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nektome-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Newly_created_provider_mints_user_id_and_has_null_connection_id()
    {
        var provider = new FileVoiceIdentityProvider(_tempDir);

        Assert.StartsWith("google-", provider.GetUserId());
        Assert.Null(provider.GetLastConnectionId());
    }

    [Fact]
    public void ResetIdentity_mints_new_user_id_and_clears_connection()
    {
        var provider = new FileVoiceIdentityProvider(_tempDir);
        provider.SaveLastConnectionId("old-conn");
        string firstId = provider.GetUserId();

        string newId = provider.ResetIdentity();
        Assert.NotEqual(firstId, newId);
        Assert.StartsWith("google-", newId);
        Assert.Null(provider.GetLastConnectionId());
    }

    [Fact]
    public void SaveLastConnectionId_persists_across_instances()
    {
        var provider1 = new FileVoiceIdentityProvider(_tempDir);
        string userId = provider1.GetUserId();

        provider1.SaveLastConnectionId("conn-abc-123");
        Assert.Equal("conn-abc-123", provider1.GetLastConnectionId());

        var provider2 = new FileVoiceIdentityProvider(_tempDir);
        Assert.Equal(userId, provider2.GetUserId());
        Assert.Equal("conn-abc-123", provider2.GetLastConnectionId());
    }

    [Fact]
    public void VoiceOptions_locale_defaults_to_system_current_ui_culture()
    {
        var options = new VoiceOptions();
        Assert.Equal(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, options.Locale);
    }

    [Fact]
    public async Task SocketIoVoiceTransport_sends_handshake_headers()
    {
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:19286/");
        listener.Start();

        var transport = new NektoMe.Infrastructure.Transports.SocketIoVoiceTransport(new VoiceOptions());
        var connectTask = transport.ConnectAsync(new NektoMe.Application.Abstractions.AudioServerEndpoint(
            new Uri("http://127.0.0.1:19286/"), "/androiduk"));

        var ctx = await listener.GetContextAsync();
        var headers = ctx.Request.Headers;

        Assert.Equal("2", headers["NektoMe-Chat-Version"]);
        Assert.Equal("1.7.1", headers["App-Android-Version"]);
        Assert.Equal("96", headers["App-Android-Code"]);
        Assert.NotNull(headers["Android-Language"]);
        Assert.NotNull(headers["User-Agent"]);
        Assert.Contains("NektoMeAudio96", headers["User-Agent"]);

        listener.Stop();
        transport.Dispose();
    }

    [Fact]
    public void VoiceDevicePool_generates_valid_android_user_agents()
    {
        var device = NektoMe.Infrastructure.Transports.VoiceDevicePool.GetRandomDevice();
        Assert.NotNull(device.Model);
        Assert.NotNull(device.Build);
        Assert.Contains("NektoMeAudio96/2.1.0 (Linux; U; Android", device.UserAgent);
    }

    [Fact]
    public void VoiceTransportOptions_creates_web_proxy_from_url()
    {
        var options = new NektoMe.Infrastructure.Transports.VoiceTransportOptions
        {
            ProxyUrl = "http://127.0.0.1:8080"
        };
        var proxy = options.CreateWebProxy();
        Assert.NotNull(proxy);
        Assert.Equal("http://127.0.0.1:8080/", proxy.GetProxy(new Uri("http://example.com"))?.ToString());

        var randomized = options.RandomizeDevice();
        Assert.NotNull(randomized.Model);
        Assert.Equal(options.UserAgent, randomized.UserAgent);
    }

    [Fact]
    public void VoiceTransportOptions_defaults_to_EngineIO_V3_and_can_be_mutated()
    {
        var options = new NektoMe.Infrastructure.Transports.VoiceTransportOptions();
        Assert.Equal(EngineIO.V3, options.Engine);

        options.Engine = EngineIO.V4;
        Assert.Equal(EngineIO.V4, options.Engine);
    }

    [Fact]
    public void VoiceDevicePool_endpoint_path_is_androiduk()
    {
        string path = NektoMe.Infrastructure.Transports.VoiceDevicePool.GetRandomEndpointPath();
        Assert.Equal("/androiduk", path);
    }

    [Fact]
    public void ResetIdentity_with_prefix_mints_custom_prefix_and_clears_connection()
    {
        var provider = new FileVoiceIdentityProvider(_tempDir);
        provider.SaveLastConnectionId("conn-123");

        string huaweiId = provider.ResetIdentity("huawei");
        Assert.StartsWith("huawei-", huaweiId);
        Assert.Null(provider.GetLastConnectionId());

        provider.SetUserId("custom-user-999");
        Assert.Equal("custom-user-999", provider.GetUserId());
        Assert.Null(provider.GetLastConnectionId());
    }

    [Fact]
    public void VoiceDevicePool_generates_valid_profile_and_device()
    {
        var profile = NektoMe.Infrastructure.Transports.VoiceDevicePool.GenerateRandomProfile();
        Assert.NotNull(profile);
        Assert.True(profile.Store is "google" or "huawei");
        Assert.StartsWith(profile.Store + "-", profile.UserId);
        Assert.Contains(profile.Device.AndroidVersion, profile.Device.UserAgent);
        Assert.Contains("NektoMeAudio96/2.1.0", profile.Device.UserAgent);
        Assert.False(string.IsNullOrWhiteSpace(profile.TimeZone));
        Assert.False(string.IsNullOrWhiteSpace(profile.Locale));
    }

    [Fact]
    public void VoiceTransportOptions_proxy_pool_rotation_and_randomize_all()
    {
        var voiceOptions = new VoiceOptions();
        var transportOptions = new NektoMe.Infrastructure.Transports.VoiceTransportOptions();
        var provider = new FileVoiceIdentityProvider(_tempDir);

        transportOptions.ProxyUrl = "socks5://127.0.0.1:1080, http://127.0.0.1:8080; socks5://127.0.0.1:1082";
        Assert.Equal(3, transportOptions.ProxyPool.Count);
        Assert.Equal("socks5://127.0.0.1:1080", transportOptions.ProxyUrl);

        string? second = transportOptions.RotateProxy();
        Assert.Equal("http://127.0.0.1:8080", second);
        Assert.Equal("http://127.0.0.1:8080", transportOptions.ProxyUrl);

        var profile = transportOptions.RandomizeAll(voiceOptions, provider);
        Assert.NotNull(profile);
        Assert.Equal(profile.Device.UserAgent, transportOptions.UserAgent);
        Assert.Equal(profile.Locale, voiceOptions.Locale);
        Assert.Equal(profile.TimeZone, voiceOptions.TimeZone);
        Assert.Equal(profile.UserId, provider.GetUserId());
    }

    [Fact(Skip = "Manual live test")]
    public async Task LiveServer_BouncyCastle_Connect_Test()
    {
        var voiceOptions = new VoiceOptions();
        var transportOptions = new NektoMe.Infrastructure.Transports.VoiceTransportOptions
        {
            UseBouncyCastle = true
        };
        var transport = new NektoMe.Infrastructure.Transports.SocketIoVoiceTransport(voiceOptions, transportOptions);
        var session = new VoiceChatSession(
            transport,
            new FixedEndpointResolver(voiceOptions.StaticEndpoint!),
            new FileVoiceIdentityProvider(),
            _ => new FakeAudioEngine(),
            voiceOptions,
            pingProber: new NektoMe.Infrastructure.Media.ProcessPingProber());

        var events = new List<VoiceChatEvent>();
        using var sub = session.Events.Subscribe(e =>
        {
            Console.WriteLine($"[LIVE EVENT] {e.GetType().Name}: {JsonSerializer.Serialize((object)e)}");
            events.Add(e);
        });

        await session.ConnectAsync();
        await Task.Delay(2000);
        await session.StartSearchAsync();
        await Task.Delay(10000);
        await session.DisconnectAsync();
    }

    [Fact(Skip = "Manual live test")]
    public async Task LiveServer_Connect_Test()
    {
        var transportOptions = new NektoMe.Infrastructure.Transports.VoiceTransportOptions
        {
            UseBouncyCastle = true,
            ProxyUrl = "http://61bdc4df7893ecf1:ihwfbmS8LHp4IGuR@res.proxy-seller.com:10009"
        };
        var options = new VoiceOptions();
        var transport = new NektoMe.Infrastructure.Transports.SocketIoVoiceTransport(options, transportOptions);
        var session = new VoiceChatSession(
            transport,
            new FixedEndpointResolver(options.StaticEndpoint!),
            new FileVoiceIdentityProvider(),
            _ => new FakeAudioEngine(),
            options);

        var events = new List<VoiceChatEvent>();
        using var sub = session.Events.Subscribe(e =>
        {
            Console.WriteLine($"[LIVE EVENT] {e.GetType().Name}: {JsonSerializer.Serialize((object)e)}");
            events.Add(e);
        });

        try
        {
            await session.ConnectAsync();
            await Task.Delay(2000);
            await session.StartSearchAsync();
            await Task.Delay(5000);
            await session.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TEST ERROR] {ex}");
            throw;
        }
    }

    [Fact(Skip = "Manual live test")]
    public async Task LiveServer_WebMode_Connect_Test()
    {
        var options = new VoiceOptions
        {
            ProtocolMode = VoiceProtocolMode.Web,
            StaticEndpoint = new NektoMe.Application.Abstractions.AudioServerEndpoint(new Uri("https://audio.nekto.me/"), "/websocket")
        };
        var transport = new NektoMe.Infrastructure.Transports.SocketIoVoiceTransport(options);
        var session = new VoiceChatSession(
            transport,
            new FixedEndpointResolver(options.StaticEndpoint!),
            new FileVoiceIdentityProvider(),
            _ => new FakeAudioEngine(),
            options);

        var events = new List<VoiceChatEvent>();
        using var sub = session.Events.Subscribe(e =>
        {
            Console.WriteLine($"[LIVE WEB EVENT] {e.GetType().Name}: {JsonSerializer.Serialize((object)e)}");
            events.Add(e);
        });

        await session.ConnectAsync();
        await Task.Delay(2000);
        await session.StartSearchAsync();
        await Task.Delay(4000);
        await session.DisconnectAsync();

        Assert.Contains(events, e => e is VoiceRegistered vr && vr.Success);
    }

    [Fact]
    public void FileVoiceIdentityProvider_separates_web_token_and_auto_recovers_hex_user_id()
    {
        // Simulate corrupted file where a 32-hex web token was saved as userId
        string filePath = Path.Combine(_tempDir, "voice-user.json");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(filePath, "{\"userId\":\"ea0e7c7567dc452c86af16307e37ed8a\"}");

        var provider = new FileVoiceIdentityProvider(_tempDir);

        // Android userId must auto-recover to google-<uuid>
        Assert.StartsWith("google-", provider.GetUserId());
        // Web token should preserve the 32-hex token
        Assert.Equal("ea0e7c7567dc452c86af16307e37ed8a", provider.GetWebToken());

        // Resetting web token doesn't touch Android user id
        string oldAndroidId = provider.GetUserId();
        string newWebToken = provider.ResetWebToken();
        Assert.Equal(32, newWebToken.Length);
        Assert.Equal(oldAndroidId, provider.GetUserId());

        // Resetting Android identity doesn't touch Web token
        string newAndroidId = provider.ResetIdentity();
        Assert.StartsWith("google-", newAndroidId);
        Assert.Equal(newWebToken, provider.GetWebToken());
    }
}
