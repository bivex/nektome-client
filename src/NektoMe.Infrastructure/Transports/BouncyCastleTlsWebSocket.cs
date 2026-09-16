using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using SocketIOClient.Protocol.WebSocket;

namespace NektoMe.Infrastructure.Transports;

/// <summary>
/// WebSocket client using BouncyCastle TLS to produce a ClientHello matching
/// Android OkHttp + BoringSSL JA3 fingerprint instead of .NET macOS SecureTransport.
///
/// JA3: 771,4865-4866-4867-49195-49196-52393-49199-49200-52392-49171-49172-156-157-47-53,
///      0-23-65281-10-11-35-16-5-13-18-51-45-43-21,29-23-24,0
/// </summary>
internal sealed class BouncyCastleTlsWebSocket : IWebSocketClient
{
    private readonly WebSocketOptions _options;
    private readonly ILogger<BouncyCastleTlsWebSocket> _log;
    // Ordered list, not a Dictionary — Dictionary iteration order is not contractual
    // and the Upgrade request must preserve the exact header order of the Android APK.
    private readonly List<KeyValuePair<string, string>> _extraHeaders = new();

    private TcpClient? _tcp;
    private TlsClientProtocol? _tlsProtocol;
    private Stream? _tlsStream;
    private WebSocketState _state = WebSocketState.None;
    private int _continuationOpcode = 0x01;
    private byte[]? _leftoverPayload;
    private int _leftoverOffset;
    private bool _leftoverFin;
    private System.Net.WebSockets.WebSocketMessageType _leftoverMsgType;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    // Cancelled in Dispose() to immediately unblock all pending ReadAsync calls
    private readonly CancellationTokenSource _disposeCts = new();

    public BouncyCastleTlsWebSocket(WebSocketOptions options,
        ILogger<BouncyCastleTlsWebSocket>? logger = null)
    {
        _options = options;
        _log = logger ?? NullLogger<BouncyCastleTlsWebSocket>.Instance;
    }

    public WebSocketState State => _state;

    public void SetDefaultHeader(string name, string value)
    {
        // Update an existing entry in place so replacement headers keep their original position
        int index = _extraHeaders.FindIndex(h =>
            string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            _extraHeaders[index] = new KeyValuePair<string, string>(name, value);
        else
            _extraHeaders.Add(new KeyValuePair<string, string>(name, value));
    }

    private void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        Console.Error.WriteLine(line);
        _log.LogInformation("{Msg}", msg);
    }

    private void LogErr(string msg, Exception? ex = null)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] ❌ {msg}{(ex != null ? $": {ex.Message}" : "")}";
        Console.Error.WriteLine(line);
        _log.LogError(ex, "{Msg}", msg);
    }

    // ── Connect ───────────────────────────────────────────────────────────────

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Connecting;
        Log($"[BC-WS] ConnectAsync → {uri}");

        int port = uri.Port > 0 ? uri.Port : (uri.Scheme == "wss" ? 443 : 80);
        Stream networkStream;
        Uri? proxyUri = _options.Proxy?.GetProxy(uri);
        bool useProxy = proxyUri != null && proxyUri != uri;

        _tcp = new TcpClient { NoDelay = true };
        try
        {
            if (useProxy)
            {
                Log($"[BC-WS] Using proxy: {proxyUri!.Scheme}://{proxyUri.Host}:{proxyUri.Port}");
                await _tcp.ConnectAsync(proxyUri.Host, proxyUri.Port, cancellationToken).ConfigureAwait(false);
                networkStream = _tcp.GetStream();

                if (proxyUri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    await EstablishHttpTunnelAsync(networkStream, proxyUri, _options.Proxy?.Credentials, uri.Host, port, cancellationToken).ConfigureAwait(false);
                }
                else if (proxyUri.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase))
                {
                    await EstablishSocks5TunnelAsync(networkStream, proxyUri, _options.Proxy?.Credentials, uri.Host, port, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                Log($"[BC-WS] TCP connect → {uri.Host}:{port}");
                await _tcp.ConnectAsync(uri.Host, port, cancellationToken).ConfigureAwait(false);
                networkStream = _tcp.GetStream();
            }
        }
        catch (Exception ex)
        {
            LogErr("[BC-WS] Connection FAILED", ex);
            throw;
        }
        Log("[BC-WS] TCP connected ✓");

        bool useTls = uri.Scheme is "wss" or "https";
        if (useTls)
        {
            Log("[BC-WS] Starting BouncyCastle TLS handshake (Android JA3)…");
            var crypto = new BcTlsCrypto(new SecureRandom());
            var tlsClient = new AndroidTlsClient(crypto, uri.Host);
            _tlsProtocol = new TlsClientProtocol(networkStream);
            try
            {
                // TlsClientProtocol.Connect is a blocking read loop with no token support —
                // cancelling the token aborts the transport underneath, which unblocks the handshake.
                using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
                using var abortRegistration = tlsCts.Token.Register(() =>
                {
                    try { networkStream.Dispose(); } catch { }
                    try { _tcp?.Close(); } catch { }
                });
                await Task.Run(() => _tlsProtocol.Connect(tlsClient), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested || _disposeCts.IsCancellationRequested)
            {
                LogErr("[BC-WS] TLS handshake cancelled");
                throw new OperationCanceledException("TLS handshake was cancelled.", ex, cancellationToken);
            }
            catch (Exception ex)
            {
                LogErr("[BC-WS] TLS handshake FAILED", ex);
                throw;
            }
            _tlsStream = _tlsProtocol.Stream;
            Log("[BC-WS] TLS handshake ✓ (BouncyCastle / Android JA3)");
        }
        else
        {
            _tlsStream = networkStream;
            Log("[BC-WS] No TLS (plain TCP)");
        }

        // HTTP/1.1 Upgrade — header order matches OkHttp
        string wsKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        string upgradeReq = BuildUpgradeRequest(uri, wsKey);
        Log($"[BC-WS] HTTP Upgrade request:\n{upgradeReq}");
        byte[] reqBytes = Encoding.ASCII.GetBytes(upgradeReq);
        try
        {
            await _tlsStream.WriteAsync(reqBytes, cancellationToken).ConfigureAwait(false);
            await _tlsStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogErr("[BC-WS] Failed to send HTTP Upgrade request", ex);
            throw;
        }

        string response = await ReadHttpHeadersAsync(cancellationToken).ConfigureAwait(false);
        Log($"[BC-WS] HTTP response:\n{response}");

        ValidateUpgradeResponse(response, wsKey);

        _state = WebSocketState.Open;
        Log("[BC-WS] WebSocket OPEN ✓");
    }


    private string BuildUpgradeRequest(Uri uri, string wsKey)
    {
        var sb = new StringBuilder();
        sb.Append($"GET {uri.PathAndQuery} HTTP/1.1\r\n");
        // OkHttp omits the port for default scheme ports
        sb.Append(uri.IsDefaultPort ? $"Host: {uri.Host}\r\n" : $"Host: {uri.Host}:{uri.Port}\r\n");
        sb.Append("Upgrade: websocket\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append($"Sec-WebSocket-Key: {wsKey}\r\n");
        sb.Append("Sec-WebSocket-Version: 13\r\n");
        foreach (var (name, value) in _extraHeaders)
            sb.Append($"{name}: {value}\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    private void ValidateUpgradeResponse(string response, string wsKey)
    {
        string statusLine = response.Split("\r\n")[0].Trim();
        string[] statusParts = statusLine.Split(' ', 3);
        if (statusParts.Length < 2 || !statusParts[1].Equals("101", StringComparison.Ordinal))
        {
            LogErr($"[BC-WS] WebSocket upgrade FAILED: {statusLine}");
            throw new WebSocketException($"WebSocket upgrade failed: {statusLine}");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = response.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < lines.Length; i++)
        {
            int separator = lines[i].IndexOf(':');
            if (separator > 0)
                headers[lines[i][..separator].Trim()] = lines[i][(separator + 1)..].Trim();
        }

        // RFC 6455 §4.2.2 — Sec-WebSocket-Accept proves the peer actually speaks WebSocket
        string expectedAccept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        if (!headers.TryGetValue("Sec-WebSocket-Accept", out string? accept) || accept != expectedAccept)
        {
            LogErr("[BC-WS] WebSocket upgrade FAILED: Sec-WebSocket-Accept mismatch");
            throw new WebSocketException("WebSocket upgrade failed: Sec-WebSocket-Accept mismatch.");
        }
    }

    private async Task<string> ReadHttpHeadersAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        byte[] buf = new byte[1];
        while (true)
        {
            int r = await _tlsStream!.ReadAsync(buf, 0, 1, ct).ConfigureAwait(false);
            if (r == 0) throw new EndOfStreamException("Connection closed during HTTP upgrade.");
            sb.Append((char)buf[0]);
            int len = sb.Length;
            if (len >= 4 && sb[len - 1] == '\n' && sb[len - 2] == '\r' &&
                            sb[len - 3] == '\n' && sb[len - 4] == '\r')
                break;
        }
        return sb.ToString();
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    public async Task SendAsync(
        ArraySegment<byte> buffer,
        System.Net.WebSockets.WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        // Surface closed-transport sends to the caller instead of silently dropping
        // frames — a dropped ping/em would otherwise stall the Socket.IO session forever.
        if (_state != WebSocketState.Open || _disposeCts.IsCancellationRequested)
        {
            throw new WebSocketException($"Cannot send data because the WebSocket is {_state}, not Open.");
        }

        byte opcode = messageType == System.Net.WebSockets.WebSocketMessageType.Text ? (byte)0x01 : (byte)0x02;
        string preview = buffer.Count > 0 ? Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, Math.Min(buffer.Count, 200)) : "";
        _log.LogDebug("[BC-WS] >>> SEND opcode=0x{Opcode:X} len={Len} fin={Fin}: {Preview}",
            opcode, buffer.Count, endOfMessage, preview);

        byte[] frame = BuildFrame(opcode, endOfMessage, buffer.Array!, buffer.Offset, buffer.Count);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, _disposeCts.Token);

        await _writeLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        try
        {
            if (_disposeCts.IsCancellationRequested || _state != WebSocketState.Open || _tlsStream is null)
            {
                throw new WebSocketException($"Cannot send data because the WebSocket is {_state}, not Open.");
            }

            _tlsStream.Write(frame, 0, frame.Length);
            _tlsStream.Flush();
        }
        catch (Exception ex)
        {
            // A failed or timed-out write can leave a partial frame on the wire — the
            // stream is unusable. Abort so pending receives unblock and SocketIOClient
            // observes the disconnect and reconnects, instead of hanging silently.
            LogErr("[BC-WS] SendAsync failed — aborting transport", ex);
            Abort();
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }


    // ── Receive ───────────────────────────────────────────────────────────────

    public async Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (_state is WebSocketState.Closed or WebSocketState.CloseSent || _disposeCts.IsCancellationRequested)
        {
            return new WebSocketReceiveResult(0, System.Net.WebSockets.WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "Closed");
        }

        // Return unread chunks from a previously received large WebSocket frame
        if (_leftoverPayload is not null)
        {
            int remaining = _leftoverPayload.Length - _leftoverOffset;
            int copy = Math.Min(remaining, buffer.Count);
            Array.Copy(_leftoverPayload, _leftoverOffset, buffer.Array!, buffer.Offset, copy);
            _leftoverOffset += copy;
            bool eom = _leftoverOffset == _leftoverPayload.Length && _leftoverFin;
            var type = _leftoverMsgType;
            if (_leftoverOffset == _leftoverPayload.Length)
            {
                _leftoverPayload = null;
                _leftoverOffset = 0;
            }
            return new WebSocketReceiveResult(copy, type, eom);
        }

        while (!_disposeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            byte[] hdr = new byte[2];
            try
            {
                await ReadExactAsync(hdr, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new WebSocketReceiveResult(0, System.Net.WebSockets.WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "Cancelled");
            }
            catch (EndOfStreamException)
            {
                _state = WebSocketState.Closed;
                return new WebSocketReceiveResult(0, System.Net.WebSockets.WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "End of stream");
            }
            catch (Exception ex)
            {
                if (_state is WebSocketState.Closed or WebSocketState.CloseSent || _disposeCts.IsCancellationRequested)
                {
                    return new WebSocketReceiveResult(0, System.Net.WebSockets.WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "Closed");
                }
                LogErr("[BC-WS] ReceiveAsync: failed reading frame header", ex);
                throw;
            }

            bool fin = (hdr[0] & 0x80) != 0;
            int opcode = hdr[0] & 0x0F;
            bool masked = (hdr[1] & 0x80) != 0;
            long payloadLen = hdr[1] & 0x7F;

            if (payloadLen == 126)
            {
                byte[] e = new byte[2];
                await ReadExactAsync(e, cancellationToken).ConfigureAwait(false);
                payloadLen = (e[0] << 8) | e[1];
            }
            else if (payloadLen == 127)
            {
                byte[] e = new byte[8];
                await ReadExactAsync(e, cancellationToken).ConfigureAwait(false);
                payloadLen = 0;
                foreach (byte b in e) payloadLen = (payloadLen << 8) | b;
            }

            byte[]? maskKey = null;
            if (masked)
            {
                maskKey = new byte[4];
                await ReadExactAsync(maskKey, cancellationToken).ConfigureAwait(false);
            }

            byte[] payload = new byte[payloadLen];
            if (payloadLen > 0)
                await ReadExactAsync(payload, cancellationToken).ConfigureAwait(false);

            if (masked && maskKey is not null)
                for (int i = 0; i < payload.Length; i++)
                    payload[i] ^= maskKey[i % 4];

            // Control frames
            if (opcode == 0x08) // Close
            {
                _state = WebSocketState.Closed;
                Log("[BC-WS] <<< CLOSE frame received from server");
                try { _disposeCts.Cancel(); } catch { }
                _ = Task.Run(async () =>
                {
                    try { await SendControlFrameAsync(0x08, payload, CancellationToken.None).ConfigureAwait(false); } catch { }
                });
                var status = payload.Length >= 2
                    ? (WebSocketCloseStatus)((payload[0] << 8) | payload[1])
                    : WebSocketCloseStatus.NormalClosure;
                string desc = payload.Length > 2 ? Encoding.UTF8.GetString(payload, 2, payload.Length - 2) : "";
                return new WebSocketReceiveResult(0, System.Net.WebSockets.WebSocketMessageType.Close, true, status, desc);
            }

            if (opcode == 0x09) // Ping → auto pong
            {
                _log.LogDebug("[BC-WS] <<< PING, sending PONG");
                await SendControlFrameAsync(0x0A, payload, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (opcode == 0x0A) { _log.LogDebug("[BC-WS] <<< PONG"); continue; }

            // Data frames
            if (opcode != 0x00) _continuationOpcode = opcode;
            var msgType = _continuationOpcode == 0x01
                ? System.Net.WebSockets.WebSocketMessageType.Text
                : System.Net.WebSockets.WebSocketMessageType.Binary;

            if (payloadLen > buffer.Count)
            {
                Array.Copy(payload, 0, buffer.Array!, buffer.Offset, buffer.Count);
                _leftoverPayload = payload;
                _leftoverOffset = buffer.Count;
                _leftoverFin = fin;
                _leftoverMsgType = msgType;
                return new WebSocketReceiveResult(buffer.Count, msgType, endOfMessage: false);
            }

            int copyLen = (int)payloadLen;
            if (copyLen > 0)
                Array.Copy(payload, 0, buffer.Array!, buffer.Offset, copyLen);

            string recvPreview = copyLen > 0 ? Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, Math.Min(copyLen, 200)) : "";
            _log.LogDebug("[BC-WS] <<< RECV opcode=0x{Opcode:X} len={Len} fin={Fin}: {Preview}",
                opcode, copyLen, fin, recvPreview);

            return new WebSocketReceiveResult(copyLen, msgType, fin);
        }

        return new WebSocketReceiveResult(0, System.Net.WebSockets.WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "Closed");
    }

    // ── Close ─────────────────────────────────────────────────────────────────

    public async Task CloseAsync(WebSocketCloseStatus closeStatus, string desc, CancellationToken cancellationToken)
    {
        Log($"[BC-WS] CloseAsync state={_state} status={closeStatus}");
        if (_state == WebSocketState.Closed) return;
        _state = WebSocketState.CloseSent;

        try
        {
            byte[] statusBytes = [(byte)((int)closeStatus >> 8), (byte)((int)closeStatus & 0xFF)];
            byte[] reasonBytes = Encoding.UTF8.GetBytes(desc ?? "");
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await SendControlFrameAsync(0x08, [.. statusBytes, .. reasonBytes], cts.Token).ConfigureAwait(false);
        }
        catch { /* ignore */ }

        // Abort the remaining transport: cancel _disposeCts to unblock pending reads, then close TCP
        Abort();
    }

    public void Dispose()
    {
        Log($"[BC-WS] Dispose state={_state}");
        Abort();

        try { _tlsStream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }

        // _disposeCts and _writeLock are still referenced by in-flight ReceiveAsync/SendAsync
        // calls. Cancellation + TCP close unblock them immediately, so dispose after a short
        // grace period instead of synchronously (avoids ObjectDisposedException in the unwind).
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(1000).ConfigureAwait(false); } catch { }
            try { _disposeCts.Dispose(); } catch { }
            try { _writeLock.Dispose(); } catch { }
        });
    }


    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Marks the transport dead and tears down the socket. Pending reads unblock via
    /// <see cref="_disposeCts"/>; never call this while holding <see cref="_writeLock"/>
    /// disposal is done in <see cref="Dispose"/>, this only cancels.
    /// </summary>
    private void Abort()
    {
        _state = WebSocketState.Closed;
        try { _disposeCts.Cancel(); } catch { }
        try { _tcp?.Client?.Shutdown(System.Net.Sockets.SocketShutdown.Both); } catch { }
        try { _tcp?.Close(); } catch { }
    }

    private async Task SendControlFrameAsync(byte opcode, byte[] payload, CancellationToken ct)
    {
        byte[] frame = BuildFrame(opcode, true, payload, 0, payload.Length);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _tlsStream!.WriteAsync(frame, ct).ConfigureAwait(false);
            await _tlsStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Control frames are best-effort; the data path surfaces transport failures
            _log.LogDebug(ex, "[BC-WS] Control frame 0x{Opcode:X} send failed", opcode);
        }
        finally { _writeLock.Release(); }
    }

    private static byte[] BuildFrame(byte opcode, bool fin, byte[] data, int offset, int count)
    {
        byte[] maskKey = RandomNumberGenerator.GetBytes(4);
        byte[] payload = new byte[count];
        for (int i = 0; i < count; i++)
            payload[i] = (byte)(data[offset + i] ^ maskKey[i % 4]);

        using var ms = new MemoryStream(count + 14);
        ms.WriteByte((byte)((fin ? 0x80 : 0x00) | opcode));

        if (count < 126)
        {
            ms.WriteByte((byte)(0x80 | count));
        }
        else if (count <= 0xFFFF)
        {
            ms.WriteByte((byte)(0x80 | 126));
            ms.WriteByte((byte)(count >> 8));
            ms.WriteByte((byte)(count & 0xFF));
        }
        else
        {
            ms.WriteByte((byte)(0x80 | 127));
            for (int i = 7; i >= 0; i--)
                ms.WriteByte((byte)(((long)count >> (i * 8)) & 0xFF));
        }

        ms.Write(maskKey);
        ms.Write(payload);
        return ms.ToArray();
    }

    internal static async Task EstablishHttpTunnelAsync(Stream stream, Uri proxyUri, ICredentials? credentials, string targetHost, int targetPort, CancellationToken ct)
    {
        string? user = null;
        string? pass = null;

        // 1. UserInfo preserved in the proxy URI (true when WebProxy stores full URI)
        if (!string.IsNullOrEmpty(proxyUri.UserInfo))
        {
            string[] parts = proxyUri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }

        // 2. ICredentials.GetCredential() — standard path
        if (string.IsNullOrEmpty(user) && credentials != null)
        {
            var netCred = credentials.GetCredential(proxyUri, "Basic")
                       ?? credentials.GetCredential(proxyUri, string.Empty);
            if (netCred != null && !string.IsNullOrEmpty(netCred.UserName))
            {
                user = netCred.UserName;
                pass = netCred.Password;
            }
        }

        // 3. Direct cast — in case GetCredential() returns null for NetworkCredential
        if (string.IsNullOrEmpty(user) && credentials is NetworkCredential nc && !string.IsNullOrEmpty(nc.UserName))
        {
            user = nc.UserName;
            pass = nc.Password;
        }

        string? authHeader = null;
        if (!string.IsNullOrEmpty(user))
        {
            authHeader = "Proxy-Authorization: Basic " +
                         Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")) + "\r\n";
        }

        string resp = await SendConnectAsync(stream, targetHost, targetPort, authHeader, ct).ConfigureAwait(false);
        string statusLine = resp.Split("\r\n")[0];

        if (!statusLine.Contains("200"))
        {
            throw new WebSocketException($"HTTP Proxy CONNECT failed: {statusLine.Trim()}");
        }
    }

    private static async Task<string> SendConnectAsync(Stream stream, string targetHost, int targetPort, string? authHeader, CancellationToken ct)
    {
        var connectReq = new StringBuilder();
        connectReq.Append($"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\n");
        connectReq.Append($"Host: {targetHost}:{targetPort}\r\n");
        if (authHeader != null)
            connectReq.Append(authHeader);
        connectReq.Append("Proxy-Connection: Keep-Alive\r\n\r\n");

        byte[] reqBytes = Encoding.ASCII.GetBytes(connectReq.ToString());
        await stream.WriteAsync(reqBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var responseBuffer = new StringBuilder();
        byte[] b = new byte[1];
        while (true)
        {
            int r = await stream.ReadAsync(b, 0, 1, ct).ConfigureAwait(false);
            if (r == 0) throw new EndOfStreamException("Proxy closed connection unexpectedly during HTTP CONNECT.");
            responseBuffer.Append((char)b[0]);
            if (responseBuffer.Length >= 4 &&
                responseBuffer[responseBuffer.Length - 4] == '\r' &&
                responseBuffer[responseBuffer.Length - 3] == '\n' &&
                responseBuffer[responseBuffer.Length - 2] == '\r' &&
                responseBuffer[responseBuffer.Length - 1] == '\n')
            {
                break;
            }
        }
        return responseBuffer.ToString();
    }

    internal static async Task EstablishSocks5TunnelAsync(Stream stream, Uri proxyUri, ICredentials? credentials, string targetHost, int targetPort, CancellationToken ct)
    {
        string? user = null;
        string? pass = null;
        if (!string.IsNullOrEmpty(proxyUri.UserInfo))
        {
            string[] parts = proxyUri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }
        else if (credentials != null)
        {
            var netCred = credentials.GetCredential(proxyUri, "Basic") ?? credentials.GetCredential(proxyUri, "");
            if (netCred != null)
            {
                user = netCred.UserName;
                pass = netCred.Password;
            }
        }

        bool hasAuth = !string.IsNullOrEmpty(user);
        byte[] greeting = hasAuth ? [0x05, 0x02, 0x00, 0x02] : [0x05, 0x01, 0x00];
        await stream.WriteAsync(greeting, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        byte[] methodResponse = new byte[2];
        await ReadExactStreamAsync(stream, methodResponse, ct).ConfigureAwait(false);
        if (methodResponse[0] != 0x05)
            throw new WebSocketException($"Invalid SOCKS5 version response: {methodResponse[0]}");

        byte chosenMethod = methodResponse[1];
        if (chosenMethod == 0xFF)
            throw new WebSocketException("SOCKS5 proxy rejected authentication methods.");

        if (chosenMethod == 0x02)
        {
            if (!hasAuth)
                throw new WebSocketException("SOCKS5 proxy requested authentication, but no credentials were provided.");

            byte[] userBytes = Encoding.UTF8.GetBytes(user!);
            byte[] passBytes = Encoding.UTF8.GetBytes(pass ?? string.Empty);

            byte[] authReq = new byte[3 + userBytes.Length + passBytes.Length];
            authReq[0] = 0x01; // Subnegotiation version
            authReq[1] = (byte)userBytes.Length;
            Buffer.BlockCopy(userBytes, 0, authReq, 2, userBytes.Length);
            authReq[2 + userBytes.Length] = (byte)passBytes.Length;
            Buffer.BlockCopy(passBytes, 0, authReq, 3 + userBytes.Length, passBytes.Length);

            await stream.WriteAsync(authReq, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            byte[] authResp = new byte[2];
            await ReadExactStreamAsync(stream, authResp, ct).ConfigureAwait(false);
            if (authResp[1] != 0x00)
                throw new WebSocketException($"SOCKS5 proxy authentication failed (status {authResp[1]}).");
        }

        // Send CONNECT
        var ms = new MemoryStream();
        ms.WriteByte(0x05); // SOCKS5
        ms.WriteByte(0x01); // CMD: CONNECT
        ms.WriteByte(0x00); // RSV

        if (IPAddress.TryParse(targetHost, out var ip))
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                ms.WriteByte(0x01); // IPv4
                ms.Write(ip.GetAddressBytes());
            }
            else
            {
                ms.WriteByte(0x04); // IPv6
                ms.Write(ip.GetAddressBytes());
            }
        }
        else
        {
            ms.WriteByte(0x03); // Domain name
            byte[] domainBytes = Encoding.ASCII.GetBytes(targetHost);
            ms.WriteByte((byte)domainBytes.Length);
            ms.Write(domainBytes);
        }

        ms.WriteByte((byte)((targetPort >> 8) & 0xFF));
        ms.WriteByte((byte)(targetPort & 0xFF));

        byte[] connectReq = ms.ToArray();
        await stream.WriteAsync(connectReq, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        // Read response: VER(1), REP(1), RSV(1), ATYP(1)
        byte[] respHeader = new byte[4];
        await ReadExactStreamAsync(stream, respHeader, ct).ConfigureAwait(false);
        if (respHeader[0] != 0x05)
            throw new WebSocketException($"Invalid SOCKS5 connect response version: {respHeader[0]}");
        if (respHeader[1] != 0x00)
            throw new WebSocketException($"SOCKS5 connect failed with reply code 0x{respHeader[1]:X2}");

        byte atyp = respHeader[3];
        int addrLen = atyp switch
        {
            0x01 => 4,   // IPv4
            0x04 => 16,  // IPv6
            0x03 => -1,  // Domain
            _ => throw new WebSocketException($"Unknown SOCKS5 address type: {atyp}")
        };

        if (addrLen == -1)
        {
            byte[] dLen = new byte[1];
            await ReadExactStreamAsync(stream, dLen, ct).ConfigureAwait(false);
            addrLen = dLen[0];
        }

        byte[] boundAddr = new byte[addrLen + 2]; // addr + 2 port bytes
        await ReadExactStreamAsync(stream, boundAddr, ct).ConfigureAwait(false);
    }

    private static async Task ReadExactStreamAsync(Stream stream, byte[] buf, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buf.Length)
        {
            int read = await stream.ReadAsync(buf, offset, buf.Length - offset, ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Connection closed by proxy.");
            offset += read;
        }
    }

    private async Task ReadExactAsync(byte[] buf, CancellationToken ct)
    {
        // Link to _disposeCts so Dispose() cancels this immediately
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        int offset = 0;
        while (offset < buf.Length)
        {
            int read = await _tlsStream!.ReadAsync(buf, offset, buf.Length - offset, linked.Token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Connection closed by remote host.");
            offset += read;
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// BouncyCastle TLS client — Android OkHttp/BoringSSL cipher suite order
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Sends ClientHello identical to Android OkHttp + BoringSSL.
///
/// Cipher order (JA3 field): 4865,4866,4867,49195,49196,52393,49199,49200,52392,49171,49172,156,157,47,53
/// Supported groups: x25519(29), secp256r1(23), secp384r1(24)
/// Versions: TLS 1.3 + TLS 1.2
/// </summary>
internal sealed class AndroidTlsClient : DefaultTlsClient
{
    private readonly string _host;

    public AndroidTlsClient(BcTlsCrypto crypto, string host) : base(crypto)
    {
        _host = host;
    }

    // SNI — send server hostname in ClientHello
    protected override IList<ServerName> GetSniServerNames() =>
        new List<ServerName> { new ServerName(NameType.host_name, System.Text.Encoding.ASCII.GetBytes(_host)) };

    public override int[] GetCipherSuites() =>
    [
        CipherSuite.TLS_AES_128_GCM_SHA256,
        CipherSuite.TLS_AES_256_GCM_SHA384,
        CipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        CipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        CipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,
        CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA,
        CipherSuite.TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA,
        CipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256,
        CipherSuite.TLS_RSA_WITH_AES_256_GCM_SHA384,
        CipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA,
        CipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA,
    ];

    protected override ProtocolVersion[] GetSupportedVersions() =>
        ProtocolVersion.TLSv13.DownTo(ProtocolVersion.TLSv12);

    public override TlsAuthentication GetAuthentication() => new AcceptAllTlsAuthentication();

    public override IDictionary<int, byte[]> GetClientExtensions()
    {
        IDictionary<int, byte[]> extensions = base.GetClientExtensions() ?? new Dictionary<int, byte[]>();
        // x25519 → secp256r1 → secp384r1 (same order as Android BoringSSL)
        TlsExtensionsUtilities.AddSupportedGroupsExtension(extensions,
            [NamedGroup.x25519, NamedGroup.secp256r1, NamedGroup.secp384r1]);
        return extensions;
    }
}

internal sealed class AcceptAllTlsAuthentication : TlsAuthentication
{
    public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest) => null;
    public void NotifyServerCertificate(TlsServerCertificate serverCertificate) { }
}
