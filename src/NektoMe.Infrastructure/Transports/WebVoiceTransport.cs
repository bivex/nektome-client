using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NektoMe.Application;
using NektoMe.Application.Abstractions;
using NektoMe.Application.Protocol;

namespace NektoMe.Infrastructure.Transports;

/// <summary>
/// Signaling transport for the Web (browser) version of NektoMe audiochat.
/// Uses raw WebSocket with flat JSON protocol (no Socket.IO wrapper), browser headers,
/// and automated Web antibot challenge / fingerprint handling.
/// </summary>
public sealed class WebVoiceTransport : IVoiceTransport
{
    private const string DefaultBrowserUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string DefaultOrigin = "https://nekto.me";

    private readonly VoiceOptions _options;
    private readonly VoiceTransportOptions _transportOptions;
    private readonly ILogger<WebVoiceTransport> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _gate = new();

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private bool _isDisposed;

    public WebVoiceTransport(
        VoiceOptions options,
        VoiceTransportOptions? transportOptions = null,
        ILogger<WebVoiceTransport>? logger = null)
    {
        _options = options;
        _transportOptions = transportOptions ?? new VoiceTransportOptions();
        _logger = logger ?? NullLogger<WebVoiceTransport>.Instance;
    }

    public event Action? Connected;
    public event Action<string?>? Disconnected;
#pragma warning disable CS0067
    public event Action? Reconnecting;
#pragma warning restore CS0067
    public event Action<string>? TransportError;
    public event Action<VoiceWireEvent>? EventReceived;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _ws?.State == WebSocketState.Open;
            }
        }
    }

    public async Task ConnectAsync(AudioServerEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ws = new ClientWebSocket();

        string userAgent = DefaultBrowserUserAgent;
        ws.Options.SetRequestHeader("User-Agent", userAgent);
        ws.Options.SetRequestHeader("Origin", DefaultOrigin);
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        if (_transportOptions.CreateWebProxy() is { } proxy)
        {
            ws.Options.Proxy = proxy;
        }

        Uri uri = BuildWebSocketUri(endpoint);
        _logger.LogInformation("[Web-WS] Connecting to {Uri}...", uri);

        try
        {
            await ws.ConnectAsync(uri, cts.Token).ConfigureAwait(false);
            _logger.LogInformation("[Web-WS] Connected to Web signaling server ✓");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Web-WS] Connection failed: {Message}", ex.Message);
            ws.Dispose();
            cts.Dispose();
            TransportError?.Invoke($"Web-WS connection failed: {ex.Message}");
            throw;
        }

        lock (_gate)
        {
            _ws = ws;
            _cts = cts;
            _receiveTask = Task.Run(() => ReceiveLoopAsync(ws, userAgent, cts.Token));
        }

        Connected?.Invoke();
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ClientWebSocket? ws;
        CancellationTokenSource? cts;
        Task? recvTask;

        lock (_gate)
        {
            ws = _ws;
            cts = _cts;
            recvTask = _receiveTask;

            _ws = null;
            _cts = null;
            _receiveTask = null;
        }

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
        }

        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", timeoutCts.Token).ConfigureAwait(false);
                }
            }
            catch { }
            finally
            {
                ws.Dispose();
            }
        }

        if (cts is not null)
        {
            cts.Dispose();
        }

        Disconnected?.Invoke("Client disconnected");
    }

    public async Task SendAsync(VoiceWireEvent message, CancellationToken cancellationToken = default)
    {
        ClientWebSocket? ws;
        lock (_gate)
        {
            ws = _ws;
        }

        if (ws is null || ws.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Web voice socket is not connected.");
        }

        string rawText = message.Root.GetRawText();
        byte[] bytes = Encoding.UTF8.GetBytes(rawText);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ws.State != WebSocketState.Open) return;
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("[Web-WS] >>> {Type}: {Bytes} bytes", message.Type, bytes.Length);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, string userAgent, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var ms = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("[Web-WS] Server closed WebSocket: {CloseStatus}", result.CloseStatus);
                        Disconnected?.Invoke(result.CloseStatusDescription ?? "Server closed connection");
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                string jsonText = Encoding.UTF8.GetString(ms.ToArray());
                _logger.LogDebug("[Web-WS] <<< {Text}", jsonText.Length > 200 ? jsonText[..200] : jsonText);

                await ProcessIncomingMessageAsync(jsonText, userAgent, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Web-WS] ReceiveLoop error: {Message}", ex.Message);
            TransportError?.Invoke(ex.Message);
            Disconnected?.Invoke(ex.Message);
        }
    }

    private async Task ProcessIncomingMessageAsync(string jsonText, string userAgent, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            if (!doc.RootElement.TryGetProperty("type", out var typeElem))
            {
                return;
            }

            string type = typeElem.GetString() ?? string.Empty;
            JsonElement root = doc.RootElement.Clone();

            // Antibot automatic handling
            switch (type)
            {
                case "challenge" or "challenge-request" or "challenge-proof":
                {
                    _logger.LogInformation("[Web-WS] Received challenge '{Type}', sending challenge-proof...", type);
                    VoiceWireEvent proof = WebAntibotHelper.BuildChallengeProof(root);
                    await SendAsync(proof, ct).ConfigureAwait(false);
                    return;
                }

                case "challenge-sync":
                {
                    _logger.LogInformation("[Web-WS] Received challenge-sync, sending challenge-ack...");
                    VoiceWireEvent ack = WebAntibotHelper.BuildChallengeAck(root);
                    await SendAsync(ack, ct).ConfigureAwait(false);
                    return;
                }

                case "challenge-trace":
                {
                    _logger.LogInformation("[Web-WS] Received challenge-trace, sending challenge-trace response...");
                    VoiceWireEvent trace = WebAntibotHelper.BuildChallengeTrace(root);
                    await SendAsync(trace, ct).ConfigureAwait(false);
                    return;
                }

                case "challenge-ack":
                    // Server acknowledgment, no reply needed
                    return;

                case "ping-server-request" when root.TryGetProperty("echo", out _):
                {
                    _logger.LogInformation("[Web-WS] Received ping-server-request with echo, sending response...");
                    VoiceWireEvent pong = WebAntibotHelper.BuildPingServerResponse(root);
                    await SendAsync(pong, ct).ConfigureAwait(false);
                    break;
                }

                case "registered":
                {
                    // Raise registered event to application first
                    EventReceived?.Invoke(new VoiceWireEvent(type, root));

                    bool success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                    if (success)
                    {
                        // On registered: immediately send set-fpt fingerprint
                        _logger.LogInformation("[Web-WS] Registered successfully! Sending set-fpt browser fingerprint...");
                        string token = root.TryGetProperty("connectionId", out var cid) ? cid.GetString() ?? "" : "";
                        VoiceWireEvent fpt = WebAntibotHelper.BuildSetFpt(userAgent, token, _options.Locale ?? "ru");
                        await SendAsync(fpt, ct).ConfigureAwait(false);
                    }
                    return;
                }
            }

            // Forward to business session
            EventReceived?.Invoke(new VoiceWireEvent(type, root));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Web-WS] Failed to parse message: {Message}", ex.Message);
        }
    }

    private static Uri BuildWebSocketUri(AudioServerEndpoint endpoint)
    {
        string baseStr = endpoint.Url.ToString();
        if (baseStr.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            baseStr = "ws://" + baseStr[7..];
        }
        else if (baseStr.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseStr = "wss://" + baseStr[8..];
        }

        var ub = new UriBuilder(baseStr);
        string path = endpoint.Path;
        if (!string.IsNullOrEmpty(path))
        {
            ub.Path = ub.Path.TrimEnd('/') + '/' + path.TrimStart('/');
        }
        return ub.Uri;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _ = DisconnectAsync();
        _sendLock.Dispose();
    }
}
