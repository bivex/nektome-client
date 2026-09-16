using System.Text.Json;
using System.Text.Json.Serialization;
using NektoMe.Application.Abstractions;

namespace NektoMe.Infrastructure.Persistence;

/// <summary>
/// Voice-service identity persisted under ~/.nektome-client/voice-user.json,
/// mirroring the reference client's "&lt;store&gt;-&lt;id&gt;" scheme with a
/// "desktop" prefix minted on first run. The last server-issued connection id
/// is stored alongside the user id so a restart registers with
/// <c>connectionIdLast</c>, exactly like the reference client.
/// </summary>
public sealed class FileVoiceIdentityProvider : IVoiceIdentityProvider
{
    private const string Prefix = "google";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private StoredIdentity _identity = null!;

    public FileVoiceIdentityProvider(string? directory = null)
    {
        _path = Path.Combine(directory ?? FileTokenStore.DefaultDirectory, "voice-user.json");
        LoadOrCreate();
    }

    public string GetUserId()
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_identity.UserId) ||
                (_identity.UserId.Length == 32 && !(_identity.UserId.Contains('-'))))
            {
                return ResetIdentity();
            }
            return _identity.UserId;
        }
    }

    public string? GetLastConnectionId()
    {
        lock (_gate)
        {
            return _identity.ConnectionId;
        }
    }

    public void SaveLastConnectionId(string connectionId)
    {
        lock (_gate)
        {
            if (_identity.ConnectionId == connectionId)
            {
                return;
            }

            StoredIdentity updated = _identity with { ConnectionId = connectionId };
            _identity = updated;
            Persist(updated);
        }
    }

    public string ResetIdentity(string? prefix = null)
    {
        lock (_gate)
        {
            string storePrefix = string.IsNullOrWhiteSpace(prefix)
                ? Prefix
                : prefix.Trim();
            StoredIdentity minted = _identity is null
                ? new StoredIdentity($"{storePrefix}-{Guid.NewGuid():D}", null, null)
                : _identity with { UserId = $"{storePrefix}-{Guid.NewGuid():D}", ConnectionId = null };
            _identity = minted;
            Persist(minted);
            return minted.UserId;
        }
    }

    public void SetUserId(string userId) => SetIdentity(userId, null);

    public void SetIdentity(string userId, string? connectionId = null)
    {
        lock (_gate)
        {
            StoredIdentity updated = _identity with { UserId = userId, ConnectionId = connectionId };
            _identity = updated;
            Persist(updated);
        }
    }

    public string GetWebToken()
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_identity.WebToken))
            {
                string token = Guid.NewGuid().ToString("N");
                _identity = _identity with { WebToken = token };
                Persist(_identity);
                return token;
            }
            return _identity.WebToken;
        }
    }

    public string ResetWebToken()
    {
        lock (_gate)
        {
            string token = Guid.NewGuid().ToString("N");
            _identity = _identity with { WebToken = token };
            Persist(_identity);
            return token;
        }
    }

    public void SetWebToken(string token)
    {
        lock (_gate)
        {
            _identity = _identity with { WebToken = token.Trim() };
            Persist(_identity);
        }
    }

    private void LoadOrCreate()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var stored = JsonSerializer.Deserialize<StoredIdentity>(File.ReadAllText(_path), Json);
                    if (stored is not null)
                    {
                        string? webToken = stored.WebToken;
                        string userId = stored.UserId;

                        // If stored userId is a 32-hex web token, migrate it to webToken
                        if (!string.IsNullOrEmpty(userId) && userId.Length == 32 && !userId.Contains('-'))
                        {
                            webToken ??= userId;
                            userId = $"{Prefix}-{Guid.NewGuid():D}";
                        }

                        if (string.IsNullOrWhiteSpace(userId))
                        {
                            userId = $"{Prefix}-{Guid.NewGuid():D}";
                        }

                        _identity = new StoredIdentity(userId, stored.ConnectionId, webToken);
                        Persist(_identity);
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // A corrupt file falls through to a fresh identity.
            }

            StoredIdentity minted = new($"{Prefix}-{Guid.NewGuid():D}", ConnectionId: null, WebToken: null);
            _identity = minted;
            Persist(minted);
        }
    }

    private void Persist(StoredIdentity identity)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(identity, Json));
        }
        catch (IOException)
        {
            // Persistence is best-effort; the id still works for this session.
        }
    }

    private sealed record StoredIdentity(
        [property: JsonPropertyName("userId")] string UserId,
        [property: JsonPropertyName("connectionId")] string? ConnectionId = null,
        [property: JsonPropertyName("webToken")] string? WebToken = null);
}
