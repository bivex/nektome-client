namespace NektoMe.Application.Abstractions;

/// <summary>Stable per-installation identity presented when requesting a token.</summary>
public sealed record DeviceIdentity(string DeviceId, string DeviceName, int DeviceType = 1);

public interface IDeviceIdentityProvider
{
    DeviceIdentity Get();
}
