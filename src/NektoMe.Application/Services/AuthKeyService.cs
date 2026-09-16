using System.Security.Cryptography;
using System.Text;
using NektoMe.Application.Abstractions;

namespace NektoMe.Application.Services;

/// <summary>
/// Derives the protocol auth <c>key</c>: an Ed25519 self-signature over
/// SHA-256(deviceId), hex encoded. On any failure the reference client
/// falls back to the literal string "null" — reproduced here.
/// </summary>
public sealed class AuthKeyService : IAuthKeyService
{
    private readonly ISecurityKeySigner _signer;

    public AuthKeyService(ISecurityKeySigner signer)
    {
        _signer = signer;
    }

    public string BuildKey(string deviceId)
    {
        try
        {
            byte[] seed = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId));
            return _signer.Sign(seed, seed);
        }
        catch (Exception)
        {
            return "null";
        }
    }
}
