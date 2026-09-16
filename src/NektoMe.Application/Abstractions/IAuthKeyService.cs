namespace NektoMe.Application.Abstractions;

/// <summary>Signs bytes and returns the wire representation (lowercase hex for Ed25519).</summary>
public interface ISecurityKeySigner
{
    string Sign(byte[] privateKeySeed, byte[] message);
}

/// <summary>Builds the "key" field sent with every authentication action.</summary>
public interface IAuthKeyService
{
    /// <summary>Derives the authentication key from the stable device id.</summary>
    string BuildKey(string deviceId);
}
