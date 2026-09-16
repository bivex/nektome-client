using System.Text.Json;
using NektoMe.Application.Abstractions;
using NektoMe.Application.Events;
using NektoMe.Application.Services;

namespace NektoMe.Application.Tests;

public sealed class FakeChatTransport : IChatTransport
{
    public List<OutboundMessage> Sent { get; } = [];
    public event Action? Connected;
    public event Action<string?>? Disconnected;
    public event Action? Reconnecting;
    public event Action<string>? TransportError;
    public event Action<NoticeMessage>? NoticeReceived;

    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
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

    public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }

    public void SimulateNotice(string name, string? dataJson = null) =>
        NoticeReceived?.Invoke(new NoticeMessage(
            name,
            dataJson is null ? null : JsonDocument.Parse(dataJson).RootElement.Clone(),
            null,
            null));

    public void SimulateDisconnect(string? reason = null)
    {
        IsConnected = false;
        Disconnected?.Invoke(reason);
    }

    public void SimulateReconnecting() => Reconnecting?.Invoke();

    public void SimulateTransportError(string message) => TransportError?.Invoke(message);

    public string LastSentJson => Sent[^1].Payload.GetRawText();

    public void Dispose()
    {
    }
}

public sealed class FixedSigner(string result = "signature") : ISecurityKeySigner
{
    public List<byte[]> Seeds { get; } = [];

    public string Sign(byte[] privateKeySeed, byte[] message)
    {
        Seeds.Add(privateKeySeed);
        return result;
    }
}

public sealed class ThrowingSigner : ISecurityKeySigner
{
    public string Sign(byte[] privateKeySeed, byte[] message) => throw new InvalidOperationException("boom");
}

public sealed class MemoryTokenStore : ITokenStore
{
    public string? Token { get; private set; }

    public string? Get() => Token;

    public void Set(string token) => Token = token;

    public void Clear() => Token = null;
}

public sealed class StaticDeviceIdentity(string deviceId = "device-1") : IDeviceIdentityProvider
{
    public DeviceIdentity Get() => new(deviceId, "Mac-mini", 1);
}

public sealed class FixedRandomIds(params long[] values) : IRandomIdGenerator
{
    private int _index;

    public long Next() => values[_index++ % values.Length];
}

public static class TestSession
{
    public static (ChatSession Session, FakeChatTransport Transport, MemoryTokenStore Tokens) Create(
        string signerResult = "deadbeef")
    {
        var transport = new FakeChatTransport();
        var tokens = new MemoryTokenStore();
        var session = new ChatSession(
            transport,
            new AuthKeyService(new FixedSigner(signerResult)),
            tokens,
            new StaticDeviceIdentity(),
            new FixedRandomIds(1001, 1002),
            new ChatOptions());
        return (session, transport, tokens);
    }
}
