using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NektoMe.Application.Events;
using NektoMe.Application.Services;
using NektoMe.Domain;

namespace NektoMe.Ui.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private const int MaxLogLines = 500;

    private readonly ChatSession _session;
    private readonly IDisposable _subscription;
    private long? _ownUserId;
    private bool _disposed;

    public MainViewModel(ChatSession session)
    {
        _session = session;
        _subscription = _session.Events.Subscribe(e => Dispatcher.UIThread.Post(() => Handle(e)));
        AppendLog("Ready. Press Connect.");
    }

    public ObservableCollection<ChatLine> Messages { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Offline";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(SearchCommand), nameof(StopSearchCommand))]
    public partial bool Connected { get; set; }

    [ObservableProperty]
    public partial string OnlineSummary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(StopSearchCommand))]
    public partial bool Searching { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(LeaveCommand))]
    public partial bool DialogOpen { get; set; }

    [ObservableProperty]
    public partial string TypingIndicator { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string InputText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyCaptchaCommand))]
    public partial bool CaptchaVisible { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyCaptchaCommand))]
    public partial string CaptchaSolution { get; set; } = string.Empty;

    // -- commands -------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync(CancellationToken ct) => GuardAsync(() => _session.ConnectAsync(ct));

    private bool CanConnect() => !Connected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private Task DisconnectAsync(CancellationToken ct) => GuardAsync(() => _session.DisconnectAsync(ct));

    private bool CanDisconnect() => Connected;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchAsync(CancellationToken ct) => GuardAsync(() => _session.RunSearchAsync(cancellationToken: ct));

    private bool CanSearch() => Connected && !Searching;

    [RelayCommand(CanExecute = nameof(CanStopSearch))]
    private Task StopSearchAsync(CancellationToken ct) => GuardAsync(() => _session.StopSearchAsync(ct));

    private bool CanStopSearch() => Searching;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(CancellationToken ct)
    {
        string text = InputText;
        InputText = string.Empty;
        await GuardAsync(() => _session.SendMessageAsync(text, ct));
    }

    private bool CanSend() => DialogOpen && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanLeave))]
    private Task LeaveAsync(CancellationToken ct) => GuardAsync(() => _session.LeaveDialogAsync(ct));

    private bool CanLeave() => DialogOpen;

    [RelayCommand(CanExecute = nameof(CanVerifyCaptcha))]
    private Task VerifyCaptchaAsync(CancellationToken ct)
    {
        CaptchaVisible = false;
        string solution = CaptchaSolution;
        CaptchaSolution = string.Empty;
        return GuardAsync(() => _session.VerifyCaptchaAsync(solution, ct));
    }

    private bool CanVerifyCaptcha() => !string.IsNullOrWhiteSpace(CaptchaSolution);

    // -- event handling -------------------------------------------------------

    private void Handle(ChatEvent e)
    {
        switch (e)
        {
            case ConnectionChanged c:
                Connected = c.Connected;
                StatusText = c.Connected
                    ? "Connected — authenticating…"
                    : "Disconnected" + (c.Reason is { Length: > 0 } r ? $": {r}" : string.Empty);
                AppendLog(StatusText);
                break;

            case ReconnectingStarted:
                StatusText = "Reconnecting…";
                break;

            case TransportFailed f:
                AppendLog("⚠ " + f.Message);
                break;

            case Authenticated a:
                _ownUserId = a.UserId;
                StatusText = a.Ban.CommunicationBan
                    ? $"Authenticated #{a.UserId} — communication ban active"
                    : $"Authenticated #{a.UserId}";
                AppendLog(StatusText);
                break;

            case ErrorReceived err:
                StatusText = $"Error {err.Error.Code?.ToString() ?? "?"}: {err.Error.Description}";
                AppendLog("⚠ " + StatusText);
                break;

            case DialogOpened d:
                DialogOpen = d.Dialog.Status == DialogStatus.Open;
                Messages.Clear();
                foreach (var message in d.Dialog.Messages)
                {
                    AddMessage(message, IsOwn(message.SenderId));
                }

                AppendLog($"Dialog {d.Dialog.Id.Value} opened ({d.Dialog.Messages.Count} history, voice {(d.Dialog.SupportsVoice ? "on" : "off")}).");
                break;

            case DialogClosed dc:
                DialogOpen = false;
                AppendLog($"Dialog {dc.DialogId.Value} closed.");
                break;

            case MessageReceived msg:
                AddMessage(msg.Message, msg.OwnMessage);
                if (!msg.OwnMessage)
                {
                    _ = GuardAsync(() => _session.MarkReadAsync());
                }

                break;

            case MessagesRead:
                AppendLog("Interlocutor read your messages.");
                break;

            case TypingChanged t:
                TypingIndicator = t.Typing ? "interlocutor is typing…" : string.Empty;
                break;

            case SearchStateChanged s:
                Searching = s.Searching;
                StatusText = s.Searching ? "Searching for a partner…" : StatusText;
                break;

            case OnlineCountReceived o:
                OnlineSummary = $"Online: {o.InChats} in chats · {o.InSearch} in search";
                break;

            case CaptchaRequired cap:
                if (cap.Solution is { Length: > 0 } solution)
                {
                    AppendLog($"Captcha required — auto-verifying ({solution}).");
                    _ = GuardAsync(() => _session.VerifyCaptchaAsync(solution));
                }
                else
                {
                    CaptchaVisible = true;
                    AppendLog("Captcha required — enter a solution below.");
                }

                break;

            case DialogPaid paid:
                AppendLog(paid.Paid ? "Dialog marked as paid." : "Dialog paid flag cleared.");
                break;

            case PurchaseChanged p:
                AppendLog($"Purchase changed (paidType {p.PaidType?.ToString() ?? "-"}).");
                break;

            case ServerRequestedClose:
                AppendLog("Server requested socket close.");
                break;

            case RawNotice raw:
                AppendLog($"notice: {raw.Name}");
                break;
        }
    }

    private bool IsOwn(UserId sender) => _ownUserId is { } own && sender.Value == own;

    private void AddMessage(Message message, bool isOwn) =>
        Messages.Add(new ChatLine(message.CreatedAt, isOwn ? "you" : "anon", message.Text, isOwn));

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppendLog("⚠ " + ex.Message);
        }
    }

    private void AppendLog(string text)
    {
        Log.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
        while (Log.Count > MaxLogLines)
        {
            Log.RemoveAt(0);
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        Log.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscription.Dispose();
        _session.Dispose();
    }
}
