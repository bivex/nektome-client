using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NektoMe.Application.Services;
using NektoMe.Infrastructure.Crypto;

namespace NektoMe.Application.Tests;

public partial class CryptoTests
{
    [GeneratedRegex("^[0-9a-f]{128}$")]
    private static partial Regex Hex128();

    [Fact]
    public void Ed25519_SignatureIsLowercaseHex128()
    {
        var signer = new Ed25519AuthKeySigner();
        byte[] seed = SHA256.HashData("device-1"u8);

        string signature = signer.Sign(seed, seed);

        Assert.Matches(Hex128(), signature);
    }

    [Fact]
    public void Ed25519_SignatureIsDeterministic()
    {
        var signer = new Ed25519AuthKeySigner();
        byte[] seed = SHA256.HashData("device-1"u8);

        Assert.Equal(signer.Sign(seed, seed), signer.Sign(seed, seed));
    }

    [Fact]
    public void Ed25519_SignatureVerifiesWithPublicKey()
    {
        var signer = new Ed25519AuthKeySigner();
        byte[] seed = SHA256.HashData("device-1"u8);

        byte[] signature = Convert.FromHexString(signer.Sign(seed, seed));

        var privateKey = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(seed);
        var publicParams = privateKey.GeneratePublicKey();
        var verifier = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
        verifier.Init(false, publicParams);
        verifier.BlockUpdate(seed, 0, seed.Length);
        Assert.True(verifier.VerifySignature(signature));
    }

    [Fact]
    public void AuthKeyService_UsesSignerOutput()
    {
        var service = new AuthKeyService(new FixedSigner("abc123"));

        Assert.Equal("abc123", service.BuildKey("device-1"));
    }

    [Fact]
    public void AuthKeyService_FallsBackToNullLiteral_OnFailure()
    {
        var service = new AuthKeyService(new ThrowingSigner());

        Assert.Equal("null", service.BuildKey("device-1"));
    }
}
