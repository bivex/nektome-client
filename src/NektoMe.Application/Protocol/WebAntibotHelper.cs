using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NektoMe.Application.Abstractions;

namespace NektoMe.Application.Protocol;

/// <summary>
/// Helper for Web-version antibot handshakes (challenges and browser fingerprint)
/// reverse-engineered from nekto.me/audiochat web bundles and Grottobridolmen/nekto.
/// </summary>
public static class WebAntibotHelper
{
    private static readonly string[] Buckets = ["pulse", "echo", "mirror"];
    public const int ClientVersion = 24;

    public static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static string Sha256Hex(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Base64(string input) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(input));

    public static string Checksum(string seed, string bucket, long stamp)
    {
        string raw = Base64($"{seed}:{bucket}:{stamp}");
        return raw.Length > 48 ? raw[..48] : raw;
    }

    public static (string seed, long stamp, string bucket, string checksum, JsonElement? echo) GetCommonFields(JsonElement data)
    {
        long stamp = data.TryGetProperty("stamp", out var st) && st.TryGetInt64(out long sVal)
            ? sVal
            : NowUnix();
        string seed = data.TryGetProperty("challengeId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString()!
            : (data.TryGetProperty("seed", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()!
                : Guid.NewGuid().ToString());

        string bucket = Buckets[(int)(Math.Abs(stamp) % Buckets.Length)];
        string checksum = Checksum(seed, bucket, stamp);

        JsonElement? echo = data.TryGetProperty("echo", out var echoElem) ? echoElem.Clone() : null;

        return (seed, stamp, bucket, checksum, echo);
    }

    public static VoiceWireEvent BuildChallengeProof(JsonElement data)
    {
        var (seed, stamp, bucket, checksum, echo) = GetCommonFields(data);
        string nonce = Guid.NewGuid().ToString();
        string proofRaw = Base64($"{seed}:{bucket}:{stamp}:{nonce}");
        string proof = proofRaw.Length > 96 ? proofRaw[..96] : proofRaw;
        string mode = data.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()!
            : "sync";

        var payload = new Dictionary<string, object?>
        {
            ["type"] = "challenge-proof",
            ["challengeId"] = seed,
            ["stamp"] = stamp,
            ["bucket"] = bucket,
            ["checksum"] = checksum,
            ["clientVersion"] = ClientVersion,
            ["echo"] = echo,
            ["proofNonce"] = nonce,
            ["proof"] = proof,
            ["mode"] = mode
        };

        return new VoiceWireEvent("challenge-proof", SerializeToElement(payload));
    }

    public static VoiceWireEvent BuildChallengeAck(JsonElement data)
    {
        var (seed, stamp, bucket, checksum, echo) = GetCommonFields(data);
        string mode = data.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()!
            : "passive";

        var payload = new Dictionary<string, object?>
        {
            ["type"] = "challenge-ack",
            ["challengeId"] = seed,
            ["stamp"] = stamp,
            ["bucket"] = bucket,
            ["checksum"] = checksum,
            ["clientVersion"] = ClientVersion,
            ["echo"] = echo,
            ["mode"] = mode
        };

        return new VoiceWireEvent("challenge-ack", SerializeToElement(payload));
    }

    public static VoiceWireEvent BuildChallengeTrace(JsonElement data)
    {
        var (seed, stamp, bucket, checksum, echo) = GetCommonFields(data);
        string traceId = Guid.NewGuid().ToString();
        string signal = data.TryGetProperty("signal", out var sig) && sig.ValueKind == JsonValueKind.String
            ? sig.GetString()!
            : "hold";

        double weight = 1.0;
        if (data.TryGetProperty("weight", out var w))
        {
            if (w.ValueKind == JsonValueKind.Number && w.TryGetDouble(out double val) && !double.IsNaN(val))
            {
                weight = val;
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["type"] = "challenge-trace",
            ["challengeId"] = seed,
            ["stamp"] = stamp,
            ["bucket"] = bucket,
            ["checksum"] = checksum,
            ["clientVersion"] = ClientVersion,
            ["echo"] = echo,
            ["traceId"] = traceId,
            ["signal"] = signal,
            ["weight"] = weight
        };

        return new VoiceWireEvent("challenge-trace", SerializeToElement(payload));
    }

    public static VoiceWireEvent BuildPingServerResponse(JsonElement data)
    {
        JsonElement? echo = data.TryGetProperty("echo", out var echoElem) ? echoElem.Clone() : null;
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "ping-server-response",
            ["echo"] = echo
        };
        return new VoiceWireEvent("ping-server-response", SerializeToElement(payload));
    }

    public static VoiceWireEvent BuildSetFpt(string userAgent, string token, string locale)
    {
        string visitorId = Sha256Hex($"{userAgent}|{token}")[..32];
        string cvha = Sha256Hex($"canvas|{visitorId}")[..32];
        long now = NowUnix();

        var components = new Dictionary<string, object?>
        {
            ["cvha"] = cvha,
            ["lsa"] = true,
            ["lst"] = true,
            ["bgi"] = 1,
            ["bgh"] = 1,
            ["fcb"] = 1,
            ["lcb"] = 1,
            ["tbc"] = -1,
            ["vtkn"] = string.IsNullOrEmpty(token) ? 0 : 1,
            ["tsp"] = now,
            ["useragent"] = userAgent,
            ["isf"] = false,
            ["ref"] = null,
            ["los"] = "https://nekto.me",
            ["lsh"] = "nekto.me",
            ["aips"] = Array.Empty<string>(),
            ["symb"] = Array.Empty<string>(),
            ["isha"] = "",
            ["ifrb"] = Array.Empty<string>(),
            ["ifha"] = "",
            ["sftest"] = true,
            ["aos"] = Array.Empty<string>(),
            ["stamp"] = now,
            ["deviceInfo"] = new Dictionary<string, object?>
            {
                ["platform"] = "MacIntel",
                ["language"] = locale,
                ["languages"] = new[] { locale },
                ["cookieEnabled"] = true,
                ["doNotTrack"] = null,
                ["hardwareConcurrency"] = 8,
                ["deviceMemory"] = 8,
                ["maxTouchPoints"] = 0
            }
        };

        string infoDataJson = JsonSerializer.Serialize(components);

        var payload = new Dictionary<string, object?>
        {
            ["type"] = "set-fpt",
            ["fpt"] = visitorId,
            ["infoData"] = infoDataJson
        };

        return new VoiceWireEvent("set-fpt", SerializeToElement(payload));
    }

    public static VoiceWireEvent BuildWebAgent(string userId, long internalId)
    {
        // web-agent = base64(sha256(userId + "BYdKPTYYGZ7ALwA8oNm2" + internalId))
        string raw = $"{userId}BYdKPTYYGZ7ALwA8oNm2{internalId}";
        string hex = Sha256Hex(raw);
        string base64 = Base64(hex);
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "web-agent",
            ["data"] = base64
        };
        return new VoiceWireEvent("web-agent", SerializeToElement(payload));
    }

    public static VoiceWireEvent BuildWebFpt(string userId, long internalId, string? userAgent = null, string? timeZone = null)
    {
        userAgent ??= "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
        byte[] md5 = MD5.HashData(Encoding.UTF8.GetBytes(userId + "dildopenis"));
        string fptValue = Convert.ToHexString(md5).ToLowerInvariant();

        byte[] cipherKey = SHA256.HashData(Encoding.UTF8.GetBytes(fptValue + internalId));

        var fpt = new Dictionary<string, object?>
        {
            ["fonts"] = new Dictionary<string, object?>
            {
                ["value"] = new[] { "Calibri", "Century", "Century Gothic", "Franklin Gothic", "MS Reference Specialty", "Segoe UI Light" }
            },
            ["languages"] = new Dictionary<string, object?> { ["value"] = new[] { new[] { "ru-RU" } } },
            ["colorDepth"] = new Dictionary<string, object?> { ["value"] = 32 },
            ["deviceMemory"] = new Dictionary<string, object?> { ["value"] = 16 },
            ["screenResolution"] = new Dictionary<string, object?> { ["value"] = new[] { 1920, 1080 } },
            ["hardwareConcurrency"] = new Dictionary<string, object?> { ["value"] = 8 },
            ["timezone"] = new Dictionary<string, object?> { ["value"] = timeZone ?? TimeZoneInfo.Local.Id ?? "Europe/Kyiv" },
            ["sessionStorage"] = new Dictionary<string, object?> { ["value"] = true },
            ["localStorage"] = new Dictionary<string, object?> { ["value"] = true },
            ["indexedDB"] = new Dictionary<string, object?> { ["value"] = true },
            ["platform"] = new Dictionary<string, object?> { ["value"] = "MacIntel" },
            ["touchSupport"] = new Dictionary<string, object?>
            {
                ["value"] = new Dictionary<string, object?> { ["maxTouchPoints"] = 0, ["touchEvent"] = false, ["touchStart"] = false }
            },
            ["vendor"] = new Dictionary<string, object?> { ["value"] = "Google Inc." },
            ["cookiesEnabled"] = new Dictionary<string, object?> { ["value"] = true },
            ["lsa"] = 1,
            ["lst"] = 1,
            ["bgi"] = 1,
            ["bgh"] = 1,
            ["fcb"] = 1,
            ["lcb"] = 1,
            ["tbc"] = 1,
            ["vtkn"] = 1,
            ["tsp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["useragent"] = userAgent,
            ["stamp"] = Guid.NewGuid().ToString()
        };

        string fptJson = JsonSerializer.Serialize(fpt);
        byte[] plaintext = Encoding.UTF8.GetBytes(fptJson);

        byte[] iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = cipherKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var ms = new MemoryStream();
        using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
        {
            cs.Write(plaintext, 0, plaintext.Length);
            cs.FlushFinalBlock();
        }
        byte[] ciphertext = ms.ToArray();

        string ivBase64 = Convert.ToBase64String(iv);
        string cipherBase64 = Convert.ToBase64String(ciphertext);
        string infoDataS = $"{ivBase64}:{cipherBase64}";

        var payload = new Dictionary<string, object?>
        {
            ["type"] = "set-fpt",
            ["fpt"] = fptValue,
            ["infoDataS"] = infoDataS
        };

        return new VoiceWireEvent("set-fpt", SerializeToElement(payload));
    }

    public static JsonElement SerializeToElement(object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }
}
