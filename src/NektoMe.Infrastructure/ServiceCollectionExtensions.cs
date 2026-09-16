using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NektoMe.Application;
using NektoMe.Application.Abstractions;
using NektoMe.Application.Services;
using NektoMe.Infrastructure.Crypto;
using NektoMe.Infrastructure.Media;
using NektoMe.Infrastructure.Persistence;
using NektoMe.Infrastructure.Transports;

namespace NektoMe.Infrastructure;

/// <summary>Composition root for the chat client's ports and adapters.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNektoMeChat(
        this IServiceCollection services,
        Action<ChatOptions>? configureOptions = null,
        Action<SocketIoTransportOptions>? configureTransport = null)
    {
        var chatOptions = new ChatOptions();
        configureOptions?.Invoke(chatOptions);

        var transportOptions = new SocketIoTransportOptions();
        configureTransport?.Invoke(transportOptions);

        services.AddSingleton(chatOptions);
        services.AddSingleton(transportOptions);
        services.AddSingleton<ISecurityKeySigner, Ed25519AuthKeySigner>();
        services.AddSingleton<IAuthKeyService, AuthKeyService>();
        services.AddSingleton<ITokenStore, FileTokenStore>();
        services.AddSingleton<IDeviceIdentityProvider, FileDeviceIdentityProvider>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IRandomIdGenerator, RandomIdGenerator>();
        services.AddSingleton<IChatTransport>(sp => new SocketIoChatTransport(
            sp.GetRequiredService<ChatOptions>(),
            sp.GetRequiredService<SocketIoTransportOptions>()));
        services.AddSingleton<ChatSession>();

        return services;
    }

    public static IServiceCollection AddNektoMeVoice(
        this IServiceCollection services,
        Action<VoiceOptions>? configureOptions = null,
        Action<VoiceTransportOptions>? configureTransport = null)
    {
        var voiceOptions = new VoiceOptions();
        configureOptions?.Invoke(voiceOptions);

        var transportOptions = new VoiceTransportOptions();
        configureTransport?.Invoke(transportOptions);

        services.AddSingleton(voiceOptions);
        services.AddSingleton(transportOptions);
        services.AddSingleton<IVoiceTransport>(sp => new SwitchableVoiceTransport(
            sp.GetRequiredService<VoiceOptions>(),
            sp.GetRequiredService<VoiceTransportOptions>(),
            sp.GetService<ILoggerFactory>()));
        services.AddSingleton<IAudioServerEndpointResolver>(sp => new HttpAudioServerEndpointResolver(
            sp.GetRequiredService<VoiceOptions>()));
        services.AddSingleton<IVoiceIdentityProvider, FileVoiceIdentityProvider>();
        services.AddSingleton<Func<IAudioSink?, IAudioEngine>>(sp =>
        {
            VoiceOptions options = sp.GetRequiredService<VoiceOptions>();
            VoiceTransportOptions transport = sp.GetRequiredService<VoiceTransportOptions>();
            // TURN media rides the active proxy over TCP via a local forwarder.
            return sink => new SipsorceryAudioEngine(
                sink, RemoteDumpPath(options), () => transport.ProxyUrl);
        });
        services.AddSingleton<IMicrophoneCapture>(sp => AudioSinkFactory.CreateDefaultMicrophoneSource());
        services.AddSingleton<IPingProber, ProcessPingProber>();
        services.AddSingleton<VoiceChatSession>();

        return services;
    }

    /// <summary>
    /// Timestamps the optional remote-audio dump per chat so consecutive
    /// sessions do not overwrite each other's recordings.
    /// </summary>
    private static string? RemoteDumpPath(VoiceOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RemoteAudioDumpPath))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(options.RemoteAudioDumpPath);
        string directory = Path.GetDirectoryName(fullPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(fullPath);
        string extension = Path.GetExtension(fullPath) is { Length: > 0 } existing ? existing : ".wav";
        return Path.Combine(
            directory, $"{name}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}{extension}");
    }
}
