using System.Text.Json;
using System.Text.Json.Serialization;
using NektoMe.Application.Abstractions;

namespace NektoMe.Infrastructure.Persistence;

/// <summary>
/// Stable device identity persisted under ~/.nektome-client/device.json; a
/// random GUID is minted on first run so the server remembers this client.
/// </summary>
public sealed class FileDeviceIdentityProvider : IDeviceIdentityProvider
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly Lazy<DeviceIdentity> _identity;

    public FileDeviceIdentityProvider(string? directory = null)
    {
        _path = Path.Combine(directory ?? FileTokenStore.DefaultDirectory, "device.json");
        _identity = new Lazy<DeviceIdentity>(LoadOrCreate);
    }

    public DeviceIdentity Get() => _identity.Value;

    private DeviceIdentity LoadOrCreate()
    {
        try
        {
            if (File.Exists(_path))
            {
                var stored = JsonSerializer.Deserialize<StoredIdentity>(File.ReadAllText(_path), Json);
                if (!string.IsNullOrEmpty(stored?.DeviceId))
                {
                    return new DeviceIdentity(stored.DeviceId, stored.DeviceName ?? DefaultName());
                }
            }
        }
        catch (Exception)
        {
            // Corrupt file — fall through and mint a fresh identity.
        }

        var identity = new DeviceIdentity(Guid.NewGuid().ToString("D"), DefaultName());
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(new StoredIdentity(identity.DeviceId, identity.DeviceName), Json));
        return identity;
    }

    private static string DefaultName() => Environment.MachineName is { Length: > 0 } name ? name : "desktop";

    private sealed record StoredIdentity(
        [property: JsonPropertyName("deviceId")] string? DeviceId,
        [property: JsonPropertyName("deviceName")] string? DeviceName);
}
