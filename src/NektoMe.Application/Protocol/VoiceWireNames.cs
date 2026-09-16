namespace NektoMe.Application.Protocol;

/// <summary>
/// Voice-roulette signaling names recovered from the companion app
/// (<c>com.nektome.chatruletka.voice</c> 1.7.1): one Socket.IO channel
/// <c>"event"</c> in both directions with a flat <c>{"type": …}</c> envelope.
/// </summary>
public static class VoiceWireNames
{
    public const string Channel = "event";

    /// <summary>Handshake header signaling the voice protocol revision.</summary>
    public const string ChatVersionHeader = "NektoMe-Chat-Version";

    // Outbound
    public const string Register = "register";
    public const string ScanForPeer = "scan-for-peer";
    public const string StopScan = "stop-scan";
    public const string Offer = "offer";
    public const string Answer = "answer";
    public const string IceCandidate = "ice-candidate";
    public const string PeerDisconnect = "peer-disconnect";
    public const string PeerSoftDisconnect = "peer-soft-disconnect";
    public const string PeerMute = "peer-mute";
    public const string PeerTrouble = "peer-trouble";
    public const string StreamReceived = "stream-received";
    public const string UsersCountRequest = "users-count-request";
    public const string LogPingResults = "log-ping-results";

    // Inbound
    public const string Registered = "registered";
    public const string SearchSuccess = "search.success";
    public const string SearchOut = "search.out";
    public const string SearchStop = "search.stop";
    public const string PeerConnect = "peer-connect";
    public const string PeerConnection = "peer-connection";
    public const string UsersCount = "users-count";
    public const string CaptchaRequest = "captcha-request";
    public const string Ban = "ban";
    public const string Error = "error";
    public const string PurchaseChanged = "purchase-changed";
    public const string PingServerRequest = "ping-server-request";
}
