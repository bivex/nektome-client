using System.Text.Json;
using NektoMe.Application.Abstractions;
using NektoMe.Application.Protocol;
using NektoMe.Application.Services;

namespace NektoMe.Application.Tests;

public sealed class FakeVoiceTransport : IVoiceTransport
{
    public List<VoiceWireEvent> Sent { get; } = [];
    public event Action? Connected;
    public event Action<string?>? Disconnected;
    public event Action? Reconnecting;
    public event Action<string>? TransportError;
    public event Action<VoiceWireEvent>? EventReceived;

    public bool IsConnected { get; private set; }
    public AudioServerEndpoint? LastEndpoint { get; private set; }

    public void RaiseReconnecting() => Reconnecting?.Invoke();

    public void RaiseTransportError(string message) => TransportError?.Invoke(message);

    public Task ConnectAsync(AudioServerEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        LastEndpoint = endpoint;
        IsConnected = true;
        Connected?.Invoke();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        Disconnected?.Invoke("client");
        return Task.CompletedTask;
    }

    public Task SendAsync(VoiceWireEvent message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }

    public void SimulateConnect()
    {
        IsConnected = true;
        Connected?.Invoke();
    }

    public void SimulateEvent(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        EventReceived?.Invoke(VoiceCodec.Parse(document.RootElement.Clone()));
    }

    public void SimulateDisconnect(string? reason = null)
    {
        IsConnected = false;
        Disconnected?.Invoke(reason);
    }

    public string LastSentJson => Sent[^1].Root.GetRawText();

    public string? LastSentType => Sent.Count == 0 ? null : Sent[^1].Type;

    public void Dispose()
    {
    }
}

public sealed class FakeAudioEngine : IAudioEngine
{
    public event Action<VoiceIceCandidate>? LocalIceCandidate;
    public event Action? RemoteAudioStarted;
    public event Action<string>? StateChanged;
    public event Action<string>? IceStateChanged;
    public event Action<string>? Failed;
    public event Action<string>? Diagnostic;

    public void RaiseFailed(string reason) => Failed?.Invoke(reason);
    public void RaiseDiagnostic(string message) => Diagnostic?.Invoke(message);
    public void RaiseIceState(string state) => IceStateChanged?.Invoke(state);

    public IReadOnlyList<VoiceIceServer>? CreatedWithServers { get; private set; }
    public bool CreatedRelayOnly { get; private set; }
    public List<(string Type, string Sdp)> RemoteDescriptions { get; } = [];
    public List<VoiceIceCandidate> RemoteCandidates { get; } = [];
    public List<bool> MuteCalls { get; } = [];
    public List<short[]>? PushedPcm { get; private set; }
    public int OfferCount { get; private set; }
    public int AnswerCount { get; private set; }
    public bool Closed { get; private set; }
    public IAudioSink? SinkSeenAtCreate { get; set; }

    public Task CreateConnectionAsync(IReadOnlyList<VoiceIceServer> iceServers, bool relayOnly, CancellationToken cancellationToken = default)
    {
        CreatedWithServers = iceServers;
        CreatedRelayOnly = relayOnly;
        StateChanged?.Invoke("new");
        return Task.CompletedTask;
    }

    public Task<string> CreateOfferAsync()
    {
        OfferCount++;
        return Task.FromResult($"offer-sdp-{OfferCount}");
    }

    public Task SetRemoteDescriptionAsync(string type, string sdp)
    {
        RemoteDescriptions.Add((type, sdp));
        return Task.CompletedTask;
    }

    public Task<string> CreateAnswerAsync()
    {
        AnswerCount++;
        return Task.FromResult($"answer-sdp-{AnswerCount}");
    }

    public Task AddRemoteIceCandidateAsync(VoiceIceCandidate candidate)
    {
        RemoteCandidates.Add(candidate);
        return Task.CompletedTask;
    }

    public void SetMuted(bool muted) => MuteCalls.Add(muted);

    public void PushMicrophonePcm(short[] samples, int sampleRate) => PushedPcm = [samples];

    public void Close() => Closed = true;

    public void RaiseLocalCandidate(string mid, int index, string candidate) =>
        LocalIceCandidate?.Invoke(new VoiceIceCandidate(mid, index, candidate));

    public void RaiseRemoteAudioStarted() => RemoteAudioStarted?.Invoke();

    public void Dispose()
    {
    }
}

public sealed class StaticVoiceIdentity(string userId = "desktop-voice-1", string? initialConnectionId = null) : IVoiceIdentityProvider
{
    public string? LastSavedConnectionId { get; private set; }
    public string? ConnectionId { get; set; } = initialConnectionId;

    public string GetUserId() => userId;

    public string? GetLastConnectionId() => ConnectionId;

    public void SaveLastConnectionId(string connectionId)
    {
        ConnectionId = connectionId;
        LastSavedConnectionId = connectionId;
    }

    public string ResetIdentity(string? prefix = null)
    {
        ConnectionId = null;
        LastSavedConnectionId = null;
        return userId;
    }

    public void SetUserId(string newUserId) => SetIdentity(newUserId, null);

    public void SetIdentity(string newUserId, string? connectionId = null)
    {
        userId = newUserId;
        ConnectionId = connectionId;
        LastSavedConnectionId = null;
    }

    private string _webToken = Guid.NewGuid().ToString("N");
    public string GetWebToken() => _webToken;
    public string ResetWebToken() => _webToken = Guid.NewGuid().ToString("N");
    public void SetWebToken(string token) => _webToken = token;
}

public sealed class FakeMicrophoneCapture : IMicrophoneCapture
{
    public bool IsCapturing { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }

    public event Action<short[], int>? PcmCaptured;

    public void Start()
    {
        StartCount++;
        IsCapturing = true;
    }

    public void Stop()
    {
        StopCount++;
        IsCapturing = false;
    }

    public void RaisePcm(params short[] pcm) => PcmCaptured?.Invoke(pcm, 8000);

    public void Dispose() => Stop();
}

public sealed class FixedEndpointResolver(AudioServerEndpoint endpoint) : IAudioServerEndpointResolver
{
    public AudioServerEndpoint Endpoint { get; } = endpoint;

    public Task<AudioServerEndpoint> ResolveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Endpoint);
}

public sealed class ThrowingEndpointResolver : IAudioServerEndpointResolver
{
    public Task<AudioServerEndpoint> ResolveAsync(CancellationToken cancellationToken = default) =>
        throw new HttpRequestException("bootstrap down");
}

public sealed class FakePingProber(IReadOnlyList<VoicePingSample> samples) : IPingProber
{
    public List<(IReadOnlyList<string> Servers, int Attempts)> Calls { get; } = [];

    public Task<IReadOnlyList<VoicePingSample>> ProbeAsync(
        IReadOnlyList<string> servers,
        int attempts,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((servers, attempts));
        return Task.FromResult(samples);
    }
}

public sealed class RecordingSink : IAudioSink
{
    public List<(short[] Pcm, int SampleRate)> Writes { get; } = [];

    public void Write(short[] pcm, int sampleRate) => Writes.Add((pcm, sampleRate));
}

public static class VoiceTestHarness
{
    public static readonly AudioServerEndpoint Endpoint =
        new(new Uri("wss://voice.example/socket.io/"), "/voice");

    public static (VoiceChatSession Session, FakeVoiceTransport Transport, List<FakeAudioEngine> Engines) Create(
        VoiceOptions? options = null,
        IAudioServerEndpointResolver? resolver = null,
        IMicrophoneCapture? microphone = null,
        IPingProber? pingProber = null,
        IVoiceIdentityProvider? identity = null)
    {
        var transport = new FakeVoiceTransport();
        var engines = new List<FakeAudioEngine>();

        // Pin the time zone and locale so register payloads are deterministic.
        options ??= new VoiceOptions { TimeZone = "Europe/Moscow", Locale = "en" };

        var session = new VoiceChatSession(
            transport,
            resolver ?? new FixedEndpointResolver(Endpoint),
            identity ?? new StaticVoiceIdentity(),
            sink =>
            {
                var engine = new FakeAudioEngine { SinkSeenAtCreate = sink };
                engines.Add(engine);
                return engine;
            },
            options,
            microphone,
            pingProber);
        return (session, transport, engines);
    }
}
