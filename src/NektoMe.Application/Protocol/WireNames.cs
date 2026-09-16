namespace NektoMe.Application.Protocol;

/// <summary>Socket.IO event and protocol action/notice names recovered from the reference client.</summary>
public static class WireNames
{
    public const string EmitEvent = "action";
    public const string NoticeEvent = "notice";

    public const string SocketPath = "/android";

    // Outbound actions
    public const string AuthSendToken = "auth.sendToken";
    public const string AuthGetToken = "auth.getToken";
    public const string SearchRun = "search.run";
    public const string SearchOut = "search.sendOut";
    public const string DialogTyping = "dialog.setTyping";
    public const string DialogInfo = "dialog.info";
    public const string AnonLeaveDialog = "anon.leaveDialog";
    public const string AnonMessage = "anon.message";
    public const string AnonReadMessages = "anon.readMessages";
    public const string OnlineTrack = "online.track";
    public const string CaptchaVerify = "captcha.verify";
    public const string ReportDialog = "antispam.report";

    // Inbound notices
    public const string ErrorCode = "error.code";
    public const string AuthSuccessToken = "auth.successToken";
    public const string AuthSuccessUser = "auth.successUser";
    public const string DialogOpened = "dialog.opened";
    public const string DialogClosed = "dialog.closed";
    public const string DialogTypingNotice = "dialog.typing";
    public const string SearchSuccess = "search.success";
    public const string SearchOutNotice = "search.out";
    public const string NewMessage = "messages.new";
    public const string ReadMessages = "messages.reads";
    public const string DialogInfoNotice = "dialog.info";
    public const string DialogPaid = "dialog.paid";
    public const string OnlineCount = "online.count";
    public const string CaptchaVerifyNotice = "captcha.verify";
    public const string SocketClose = "socket.close";
    public const string PurchaseChanged = "purchase.changed";
}
