namespace NektoMe.Application;

/// <summary>Client-side configuration. Server URL default mirrors the APK's Remote Config default.</summary>
public sealed class ChatOptions
{
    public string ServerUrl { get; init; } = "https://im.nektome.online/";

    /// <summary>Two-letter locale ("en", "ru", …) sent with authentication.</summary>
    public string Locale { get; init; } = "en";

    /// <summary>IANA time zone id; defaults to the machine's zone when null.</summary>
    public string? TimeZone { get; init; }

    /// <summary>Billing/vending flag from the reference client; omitted when null.</summary>
    public string? Vending { get; init; }

    /// <summary>Legacy signature key; the reference client sends the literal "null".</summary>
    public string SigKey { get; init; } = "null";

    /// <summary>Optional push registration.</summary>
    public string? PushToken { get; init; }

    /// <summary>Push platform type (only sent together with <see cref="PushToken"/>).</summary>
    public int? PushType { get; init; }
}
