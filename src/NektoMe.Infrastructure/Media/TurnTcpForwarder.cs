using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using NektoMe.Application.Abstractions;
using NektoMe.Infrastructure.Transports;

namespace NektoMe.Infrastructure.Media;

/// <summary>
/// Local TCP forwarder that tunnels TURN-over-TCP through the chat proxy.
/// The engine rewrites <c>turn:host:3478</c> to <c>turn:127.0.0.1:{port}?transport=tcp</c>;
/// SIPSorcery then opens plain TCP connections to the loopback listener, and this
/// class dials the real TURN server through the configured HTTP CONNECT / SOCKS5
/// proxy and pumps bytes both ways. Relay candidates stay externally valid because
/// the TURN server reports its own public address in XOR-RELAYED-ADDRESS, which
/// SIPSorcery uses verbatim for the candidate.
/// </summary>
internal sealed class TurnTcpForwarder : IDisposable
{
    /// <summary>ICE server URLs use custom schemes; System.Uri won't parse the host.</summary>
    private static readonly Regex ServerPattern =
        new(@"^(turns?|stuns?):([^/?\s]+?)(?::(\d+))?(\?.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<string?> _activeProxy;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, ForwardedServer> _servers = new();
    private bool _disposed;

    public TurnTcpForwarder(Func<string?> activeProxy, Action<string>? log = null)
    {
        _activeProxy = activeProxy;
        _log = log;
    }

    /// <summary>
    /// Rewrites TURN servers to loopback forwarder endpoints. STUN servers and
    /// already-local URLs are passed through untouched; IPv6 servers are filtered
    /// out when proxying (residential proxies do not route IPv6); on failure
    /// the original server is kept.
    /// </summary>
    public IReadOnlyList<VoiceIceServer> Wrap(IReadOnlyList<VoiceIceServer> servers)
    {
        var result = new List<VoiceIceServer>(servers.Count);
        foreach (VoiceIceServer server in servers)
        {
            VoiceIceServer? wrapped = TryWrap(server);
            if (wrapped is not null)
            {
                result.Add(wrapped);
            }
        }

        return result;
    }

    /// <summary>
    /// Awaits the completion of all pre-connect tasks so that every TCP tunnel
    /// to the TURN servers is established before SIPSorcery starts sending
    /// TURN Allocate requests.  Should be called right after <see cref="Wrap"/>
    /// and before the <c>RTCPeerConnection</c> is created.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        Task[] pending = _servers.Values
            .Select(s => s.PreconnectTask)
            .Where(t => t is not null && !t.IsCompleted)
            .ToArray()!;

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Tunnels did not connect in time — SIPSorcery will still try; we just
            // could not guarantee readiness. Log nothing here: the forwarder already
            // logs per-tunnel failures inside ConnectUpstreamAsync.
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — propagate.
            throw;
        }
        catch
        {
            // Individual tunnel failures are already logged inside ConnectUpstreamAsync.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (ForwardedServer server in _servers.Values)
        {
            server.Dispose();
        }

        _servers.Clear();
    }

    private VoiceIceServer? TryWrap(VoiceIceServer server)
    {
        Match match = ServerPattern.Match(server.Url.Trim());
        if (!match.Success)
        {
            return server;
        }

        string scheme = match.Groups[1].Value.ToLowerInvariant();
        if (scheme.StartsWith("stun", StringComparison.Ordinal))
        {
            // STUN works over direct UDP (Google answers); leave it alone.
            return server;
        }

        string host = match.Groups[2].Value;
        if (IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address))
        {
            return server;
        }

        // IPv6 filter: residential proxies do not route IPv6.
        // Connecting to IPv6 TURN endpoints causes HTTP 400 Bad Request
        // and wastes SIPSorcery's gathering time limit.
        if (host.StartsWith("[", StringComparison.Ordinal) ||
            (IPAddress.TryParse(host, out IPAddress? parsedIp) && parsedIp.AddressFamily == AddressFamily.InterNetworkV6))
        {
            _log?.Invoke($"форвардер TURN: пропуск IPv6 сервера {host} (прокси IPv4)");
            return null;
        }

        int port = match.Groups[3].Success
            ? int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)
            : (scheme == "turns" ? 5349 : 3478);

        string key = $"{host}:{port}";
        try
        {
            ForwardedServer forwarded = _servers.GetOrAdd(key, _ => StartListener(host, port));
            string localUrl = scheme == "turns"
                ? $"turns:127.0.0.1:{forwarded.Port}"
                : $"turn:127.0.0.1:{forwarded.Port}?transport=tcp";
            _log?.Invoke($"форвардер TURN: {host}:{port} → 127.0.0.1:{forwarded.Port} ({(scheme == "turns" ? "TLS/TCP" : "TCP")}, через прокси)");
            return server with { Url = localUrl };
        }
        catch (Exception ex)
        {
            _log?.Invoke($"⚠ форвардер TURN: слушатель для {host}:{port} не поднят: {ex.Message}");
            return server;
        }
    }

    private ForwardedServer StartListener(string host, int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var forwarded = new ForwardedServer(listener, () => ConnectUpstreamAsync(host, port));
        _ = AcceptLoopAsync(forwarded, host, port);
        return forwarded;
    }

    private async Task AcceptLoopAsync(ForwardedServer forwarded, string host, int port)
    {
        while (!_disposed)
        {
            TcpClient client;
            try
            {
                client = await forwarded.Listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Listener stopped (dispose) — end the accept loop.
                return;
            }

            forwarded.Track(client);
            _ = HandleClientAsync(forwarded, client, host, port);
        }
    }

    private async Task HandleClientAsync(ForwardedServer forwarded, TcpClient client, string host, int port)
    {
        client.NoDelay = true;
        TcpClient? upstream = null;
        try
        {
            (upstream, Stream proxyStream) = await forwarded.GetUpstreamAsync().ConfigureAwait(false);
            _log?.Invoke($"форвардер TURN: мост 127.0.0.1:{forwarded.Port} ↔ {host}:{port} активен");

            Stream clientStream = client.GetStream();
            using var cts = new CancellationTokenSource();
            Task t1 = PipeAsync(clientStream, proxyStream, cts);
            Task t2 = PipeAsync(proxyStream, clientStream, cts);
            await Task.WhenAny(t1, t2).ConfigureAwait(false);
            cts.Cancel();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"⚠ форвардер TURN: канал до {host}:{port} прерван: {ex.Message}");
        }
        finally
        {
            upstream?.Dispose();
            client.Dispose();
            forwarded.Untrack(client);
        }
    }

    private static async Task PipeAsync(Stream source, Stream destination, CancellationTokenSource cts)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                await destination.FlushAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Closed or cancelled
        }
        finally
        {
            cts.Cancel();
        }
    }

    private async Task<(TcpClient Client, Stream Stream)?> ConnectUpstreamAsync(string host, int port)
    {
        TcpClient? upstream = null;
        try
        {
            string? proxy = _activeProxy();
            upstream = new TcpClient { NoDelay = true };

            if (string.IsNullOrWhiteSpace(proxy))
            {
                // Direct connection (no proxy)
                await upstream.ConnectAsync(host, port).ConfigureAwait(false);
                Stream directStream = upstream.GetStream();
                _log?.Invoke($"форвардер TURN: пред-подключение к {host}:{port} напрямую готово ✓");
                return (upstream, directStream);
            }

            // Proxy connection
            Uri proxyUri = ParseProxy(proxy);
            await upstream.ConnectAsync(proxyUri.Host, proxyUri.Port).ConfigureAwait(false);
            Stream proxyStream = upstream.GetStream();
            ICredentials? credentials = CredentialsFor(proxyUri);

            Task tunnel = IsSocks(proxyUri)
                ? BouncyCastleTlsWebSocket.EstablishSocks5TunnelAsync(proxyStream, proxyUri, credentials, host, port, CancellationToken.None)
                : BouncyCastleTlsWebSocket.EstablishHttpTunnelAsync(proxyStream, proxyUri, credentials, host, port, CancellationToken.None);
            await tunnel.WaitAsync(DialTimeout).ConfigureAwait(false);

            _log?.Invoke($"форвардер TURN: пред-подключение к {host}:{port} через {proxyUri.Host}:{proxyUri.Port} готово ✓");
            return (upstream, proxyStream);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"⚠ форвардер TURN: подключение к {host}:{port} не удалось: {ex.Message}");
            upstream?.Dispose();
            return null;
        }
    }

    private static bool IsSocks(Uri proxyUri) =>
        proxyUri.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase);

    private static Uri ParseProxy(string proxy)
    {
        string trimmed = proxy.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }

        return new Uri(trimmed);
    }

    private static ICredentials? CredentialsFor(Uri proxyUri)
    {
        if (string.IsNullOrEmpty(proxyUri.UserInfo))
        {
            return null;
        }

        string[] parts = proxyUri.UserInfo.Split(':', 2);
        string user = Uri.UnescapeDataString(parts[0]);
        string password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        return new NetworkCredential(user, password);
    }

    /// <summary>One listening socket per unique TURN target, plus eager pre-connection and live client tracking.</summary>
    private sealed class ForwardedServer : IDisposable
    {
        private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
        private readonly Func<Task<(TcpClient Client, Stream Stream)?>> _connectUpstream;
        private Task<(TcpClient Client, Stream Stream)?>? _preconnectTask;
        private int _preconnectConsumed;
        private bool _disposed;

        public ForwardedServer(TcpListener listener, Func<Task<(TcpClient Client, Stream Stream)?>> connectUpstream)
        {
            Listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _connectUpstream = connectUpstream;
            // Eagerly preconnect the upstream tunnel in the background
            _preconnectTask = Task.Run(connectUpstream);
        }

        public TcpListener Listener { get; }

        public int Port { get; }

        /// <summary>
        /// The background pre-connect task started at construction time.
        /// <see cref="TurnTcpForwarder.WarmUpAsync"/> awaits all of these
        /// before handing the ICE server list to SIPSorcery.
        /// </summary>
        public Task? PreconnectTask => _preconnectTask;

        public async Task<(TcpClient Client, Stream Stream)> GetUpstreamAsync()
        {
            // First check if the eager pre-connect can be claimed
            if (_preconnectTask != null && Interlocked.Exchange(ref _preconnectConsumed, 1) == 0)
            {
                try
                {
                    var pre = await _preconnectTask.ConfigureAwait(false);
                    if (pre.HasValue && pre.Value.Client.Connected)
                    {
                        return pre.Value;
                    }
                }
                catch
                {
                    // Preconnect failed; fallback to fresh connect below
                }
            }

            // Fresh on-demand connect (for subsequent connections or if preconnect failed)
            var fresh = await _connectUpstream().ConfigureAwait(false);
            if (fresh.HasValue && fresh.Value.Client.Connected)
            {
                return fresh.Value;
            }

            throw new InvalidOperationException("Failed to establish upstream TURN tunnel through proxy.");
        }

        public void Track(TcpClient client) => _clients.TryAdd(client, 0);

        public void Untrack(TcpClient client) => _clients.TryRemove(client, out _);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                Listener.Stop();
            }
            catch (Exception)
            {
                // Already stopped.
            }

            // If preconnect completed but was never consumed, dispose it
            if (_preconnectTask != null && Interlocked.Exchange(ref _preconnectConsumed, 1) == 0)
            {
                _preconnectTask.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result.HasValue)
                    {
                        t.Result.Value.Client.Dispose();
                    }
                }, TaskScheduler.Default);
            }

            foreach (TcpClient client in _clients.Keys)
            {
                try
                {
                    client.Dispose();
                }
                catch (Exception)
                {
                    // Best effort shutdown.
                }
            }

            _clients.Clear();
        }
    }
}
