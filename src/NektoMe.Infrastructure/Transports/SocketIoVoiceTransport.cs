using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NektoMe.Application;
using NektoMe.Application.Abstractions;
using NektoMe.Application.Protocol;
using SocketIOClient;
using SocketIOClient.Common;
using SocketIOClient.Protocol.WebSocket;

namespace NektoMe.Infrastructure.Transports;

/// <summary>
/// Transport-level options mirroring the reference voice app's socket setup
/// and handshake headers (version 1.7.1 / versionCode 96).
/// </summary>
public sealed class VoiceTransportOptions
{
    private readonly List<string> _proxyPool = [];
    private int _proxyIndex;

    public EngineIO Engine { get; set; } = EngineIO.V3;
    public bool Reconnection { get; init; } = true;
    public string AppVersion { get; init; } = "1.7.1";
    public string AppCode { get; init; } = "96";
    public string UserAgent { get; set; } =
        "NektoMeAudio96/2.1.0 (Linux; U; Android 14; SM-S918B Build/UP1A.231005.007)";

    /// <summary>
    /// If true (default), uses BouncyCastle TLS for Android JA3 emulation.
    /// If false, uses standard SystemClientWebSocket / native Socket.IO.
    /// </summary>
    public bool UseBouncyCastle { get; set; } = true;

    public string? RawProxyInput { get; private set; }

    public string? ProxyUrl
    {
        get => _proxyPool.Count > 0 && _proxyIndex < _proxyPool.Count ? _proxyPool[_proxyIndex] : null;
        set => SetProxyInput(value);
    }

    public IReadOnlyList<string> ProxyPool => _proxyPool;

    public int CurrentProxyIndex => _proxyIndex;

    public VoiceProfile? CurrentProfile { get; set; }

    public void SetProxyInput(string? input)
    {
        RawProxyInput = input;
        _proxyPool.Clear();
        _proxyIndex = 0;
        if (!string.IsNullOrWhiteSpace(input))
        {
            string[] items = input.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string item in items)
            {
                if (!string.IsNullOrWhiteSpace(item))
                {
                    _proxyPool.Add(item);
                }
            }
        }
    }

    public string? RotateProxy(int step = 1)
    {
        if (_proxyPool.Count == 0)
        {
            return null;
        }

        _proxyIndex = (_proxyIndex + step) % _proxyPool.Count;
        if (_proxyIndex < 0) _proxyIndex += _proxyPool.Count;
        return _proxyPool[_proxyIndex];
    }

    /// <summary>
    /// Jumps directly to the specified proxy index without rotating.
    /// </summary>
    public string? SetProxyIndex(int index)
    {
        if (_proxyPool.Count == 0) return null;
        _proxyIndex = Math.Clamp(index, 0, _proxyPool.Count - 1);
        return _proxyPool[_proxyIndex];
    }

    public string ActiveProxyDisplay
    {
        get
        {
            if (_proxyPool.Count == 0) return "Direct (без прокси)";
            string active = _proxyPool[_proxyIndex];
            if (Uri.TryCreate(active, UriKind.Absolute, out var uri))
            {
                return $"{uri.Host}:{uri.Port} ({_proxyIndex + 1}/{_proxyPool.Count})";
            }
            return $"{active} ({_proxyIndex + 1}/{_proxyPool.Count})";
        }
    }

    public IWebProxy? CreateWebProxy()
    {
        string? active = ProxyUrl;
        if (string.IsNullOrWhiteSpace(active))
        {
            return null;
        }

        var uri = new Uri(active.Trim());
        var proxy = new WebProxy(uri);
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            string[] parts = uri.UserInfo.Split(':', 2);
            string user = Uri.UnescapeDataString(parts[0]);
            string pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            proxy.Credentials = new NetworkCredential(user, pass);
        }
        return proxy;
    }

    public VoiceDeviceInfo RandomizeDevice()
    {
        VoiceDeviceInfo device = VoiceDevicePool.GetRandomDevice();
        UserAgent = device.UserAgent;
        return device;
    }

    public VoiceProfile RandomizeAll(VoiceOptions voiceOptions, IVoiceIdentityProvider? identity = null, bool rotateProxy = true)
    {
        VoiceProfile profile = VoiceDevicePool.GenerateRandomProfile();
        ApplyProfile(profile, voiceOptions, identity);
        if (rotateProxy && _proxyPool.Count > 1)
        {
            RotateProxy();
        }
        return profile;
    }

    public void ApplyProfile(VoiceProfile profile, VoiceOptions voiceOptions, IVoiceIdentityProvider? identity = null)
    {
        CurrentProfile = profile;
        UserAgent = profile.Device.UserAgent;
        voiceOptions.Locale = profile.Locale;
        voiceOptions.TimeZone = profile.TimeZone;
        Uri baseUri = voiceOptions.StaticEndpoint?.Url ?? new Uri("https://audio.nekto.me/");
        voiceOptions.StaticEndpoint = new AudioServerEndpoint(baseUri, profile.EndpointPath);

        if (identity is not null)
        {
            identity.SetUserId(profile.UserId);
        }
    }
}

/// <summary>
/// Voice signaling over Socket.IO: one <c>event</c> channel carries the flat
/// <c>{"type": …}</c> envelope in both directions, exactly like the reference
/// client's <c>socket.emit("event", JSONObject)</c> / <c>socket.on("event")</c>.
/// </summary>
public sealed class SocketIoVoiceTransport : IVoiceTransport
{
    public const string Channel = "event";

    /// <summary>
    /// Reconnect attempts tolerated before a failed route is reported upward.
    /// SocketIOClient swallows handshake failures (e.g. HTTP 502 for a burned
    /// proxy exit) inside its own reconnect loop — without this the transport
    /// hammers a dead route forever and the caller never learns anything.
    /// </summary>
    private const int MaxSilentReconnectAttempts = 4;

    private readonly VoiceOptions _options;
    private readonly VoiceTransportOptions _transportOptions;
    private readonly object _gate = new();
    private SocketIO? _socket;
    private int _attemptsSinceConnected;

    public SocketIoVoiceTransport(VoiceOptions options, VoiceTransportOptions? transportOptions = null)
    {
        _options = options;
        _transportOptions = transportOptions ?? new VoiceTransportOptions();
    }

    public event Action? Connected;

    public event Action<string?>? Disconnected;

    public event Action? Reconnecting;

    public event Action<string>? TransportError;

    public event Action<VoiceWireEvent>? EventReceived;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _socket?.Connected ?? false;
            }
        }
    }

    public async Task ConnectAsync(AudioServerEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        IWebProxy? webProxy = _transportOptions.CreateWebProxy();
        var socket = new SocketIO(endpoint.Url, new SocketIOOptions
        {
            Path = endpoint.Path,
            Transport = TransportProtocol.WebSocket,
            EIO = _options.ProtocolMode == VoiceProtocolMode.Web
                ? EngineIO.V4
                : _transportOptions.Engine,
            Reconnection = _transportOptions.Reconnection,
            // APK: options.timeout = UnityAdsConstants.Timeout.INIT_TIMEOUT_MS = 120 000 ms
            ConnectionTimeout = TimeSpan.FromSeconds(120),
            ExtraHeaders = BuildHandshakeHeaders(),
        }, services =>
        {
            var wsOptions = new WebSocketOptions();
            if (webProxy is not null)
            {
                wsOptions.Proxy = webProxy;
            }
            services.AddSingleton(wsOptions);

            // In Android mode, replace the default SystemClientWebSocket with either
            // BouncyCastleTlsWebSocket (JA3 emulation) or AndroidLikeWebSocket (disables permessage-deflate).
            // This removes fingerprint differences that trigger server-side bot detection.
            if (_options.ProtocolMode == VoiceProtocolMode.Android)
            {
                if (_transportOptions.UseBouncyCastle)
                {
                    services.AddScoped<IWebSocketClient, BouncyCastleTlsWebSocket>();
                }
                else
                {
                    services.AddScoped<IWebSocketClient, AndroidLikeWebSocket>();
                }
            }
        });
        socket.OnConnected += OnSocketConnected;
        socket.OnDisconnected += OnSocketDisconnected;
        socket.OnReconnectAttempt += OnSocketReconnectAttempt;
        socket.OnError += OnSocketError;
        socket.On(Channel, OnEvent);

        lock (_gate)
        {
            ReplaceSocket(socket);
            _socket = socket;
        }

        try
        {
            await socket.ConnectAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DetachSocketAsync(socket).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SocketIO? socket;
        lock (_gate)
        {
            socket = _socket;
            _socket = null;
        }

        if (socket is null)
        {
            return;
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await socket.DisconnectAsync(linked.Token).WaitAsync(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The socket is going away either way.
        }
        finally
        {
            await DetachSocketAsync(socket).ConfigureAwait(false);
        }
    }

    public async Task SendAsync(VoiceWireEvent message, CancellationToken cancellationToken = default)
    {
        SocketIO? socket;
        lock (_gate)
        {
            socket = _socket;
        }

        if (socket is null || !socket.Connected)
        {
            throw new InvalidOperationException("The voice socket is not connected.");
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // The JsonElement serializes as the flat event object, matching the
        // Android client's single JSONObject argument on the "event" channel.
        await socket.EmitAsync(Channel, new object[] { message.Root })
            .WaitAsync(linked.Token)
            .ConfigureAwait(false);
    }

    public void Dispose() => DisconnectAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    private void OnSocketConnected(object? sender, EventArgs e)
    {
        Volatile.Write(ref _attemptsSinceConnected, 0);
        Connected?.Invoke();
    }

    private void OnSocketDisconnected(object? sender, string reason) => Disconnected?.Invoke(reason);

    private void OnSocketReconnectAttempt(object? sender, int attempt)
    {
        Reconnecting?.Invoke();

        int failures = Interlocked.Increment(ref _attemptsSinceConnected);
        if (failures < MaxSilentReconnectAttempts || sender is not SocketIO socket)
        {
            return;
        }

        // Route is dead (burned proxy exit, offline proxy) — surface it so the
        // caller's rotation guard can walk to the next proxy, and stop the
        // library's silent hammering loop.
        Volatile.Write(ref _attemptsSinceConnected, 0);
        TransportError?.Invoke(
            $"[dead-route] сокет не смог подключиться через текущий маршрут ({failures} попыток подряд) — вероятно, IP прокси не принимается сервером");
        _ = Task.Run(() => socket.DisconnectAsync());
    }

    private void OnSocketError(object? sender, string message) => TransportError?.Invoke(message);

    private Task OnEvent(IEventContext response)
    {
        try
        {
            JsonElement root = response.GetValue<JsonElement>(0);
            EventReceived?.Invoke(VoiceCodec.Parse(root));
        }
        catch (Exception ex)
        {
            TransportError?.Invoke($"Failed to parse voice event: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Dictionary<string, string> BuildHandshakeHeaders()
    {
        if (_options.ProtocolMode == VoiceProtocolMode.Web)
        {
            return new Dictionary<string, string>
            {
                ["Origin"] = "https://nekto.me",
                ["Referer"] = "https://nekto.me/audiochat",
                ["User-Agent"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
            };
        }

        return new Dictionary<string, string>
        {
            // Exact order from Android APK's SocketClient.lambda$headers$7 (stored in java.util.TreeMap):
            // Android-Language → App-Android-Code → App-Android-Version → NektoMe-Chat-Version → User-Agent
            ["Android-Language"] = _options.Locale,
            ["App-Android-Code"] = _transportOptions.AppCode,
            ["App-Android-Version"] = _transportOptions.AppVersion,
            ["NektoMe-Chat-Version"] = _options.ChatVersion,
            ["User-Agent"] = _transportOptions.UserAgent,
        };
    }

    private void ReplaceSocket(SocketIO socket)
    {
        SocketIO? previous = _socket;
        if (previous is not null)
        {
            previous.OnConnected -= OnSocketConnected;
            previous.OnDisconnected -= OnSocketDisconnected;
            previous.OnReconnectAttempt -= OnSocketReconnectAttempt;
            previous.OnError -= OnSocketError;
            previous.Dispose();
        }
    }

    private Task DetachSocketAsync(SocketIO socket)
    {
        socket.OnConnected -= OnSocketConnected;
        socket.OnDisconnected -= OnSocketDisconnected;
        socket.OnReconnectAttempt -= OnSocketReconnectAttempt;
        socket.OnError -= OnSocketError;
        lock (_gate)
        {
            if (ReferenceEquals(_socket, socket))
            {
                _socket = null;
            }
        }

        socket.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// A <see cref="IWebSocketClient"/> that mimics OkHttp's WebSocket Upgrade request:
/// - No <c>Sec-WebSocket-Extensions: permessage-deflate</c> (OkHttp doesn't request it)
/// - <c>Accept-Encoding: gzip</c> only (not br/deflate)
/// This removes fingerprint differences that trigger server-side bot detection.
/// </summary>
internal sealed class AndroidLikeWebSocket : IWebSocketClient
{
    public AndroidLikeWebSocket(WebSocketOptions? options = null)
    {
        _ws = new ClientWebSocket();

        // Disable permessage-deflate — OkHttp does NOT send this extension.
        // .NET ClientWebSocket adds it by default which creates a detectable fingerprint.
        _ws.Options.DangerousDeflateOptions = null;

        if (options?.RemoteCertificateValidationCallback is not null)
            _ws.Options.RemoteCertificateValidationCallback = options.RemoteCertificateValidationCallback;

        if (options?.Proxy is not null)
        {
            var handler = new SocketsHttpHandler
            {
                Proxy = options.Proxy,
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(15),
            };

            if (options.Proxy is WebProxy webProxy && webProxy.Address is { } proxyAddress)
            {
                handler.ConnectCallback = async (context, ct) =>
                {
                    var tcp = new TcpClient();
                    await tcp.ConnectAsync(proxyAddress.Host, proxyAddress.Port, ct).ConfigureAwait(false);
                    Stream stream = tcp.GetStream();

                    if (proxyAddress.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase))
                    {
                        await BouncyCastleTlsWebSocket.EstablishSocks5TunnelAsync(
                            stream, proxyAddress, webProxy.Credentials, context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await BouncyCastleTlsWebSocket.EstablishHttpTunnelAsync(
                            stream, proxyAddress, webProxy.Credentials, context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    }

                    return stream;
                };
            }

            _invoker = new HttpMessageInvoker(handler);
        }
    }

    private readonly ClientWebSocket _ws;
    private readonly HttpMessageInvoker? _invoker;

    public WebSocketState State => _ws.State;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (_invoker is not null)
        {
            return _ws.ConnectAsync(uri, _invoker, cancellationToken);
        }
        return _ws.ConnectAsync(uri, cancellationToken);
    }

    public Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken cancellationToken) =>
        _ws.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        _ws.ReceiveAsync(buffer, cancellationToken);

    public Task CloseAsync(WebSocketCloseStatus closeStatus, string desc, CancellationToken cancellationToken) =>
        _ws.CloseAsync(closeStatus, desc, cancellationToken);

    public void SetDefaultHeader(string name, string value) =>
        _ws.Options.SetRequestHeader(name, value);

    public void Dispose()
    {
        _ws.Dispose();
        _invoker?.Dispose();
    }
}
