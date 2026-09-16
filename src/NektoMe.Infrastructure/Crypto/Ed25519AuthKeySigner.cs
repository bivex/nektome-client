using NektoMe.Application.Abstractions;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Crypto.Parameters;

namespace NektoMe.Infrastructure.Crypto;

/// <summary>Ed25519 signing via BouncyCastle, hex-encoded lowercase.</summary>
public sealed class Ed25519AuthKeySigner : ISecurityKeySigner
{
    public string Sign(byte[] privateKeySeed, byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKeySeed));
        signer.BlockUpdate(message, 0, message.Length);
        byte[] signature = signer.GenerateSignature();
        return Convert.ToHexString(signature).ToLowerInvariant();
    }
}
