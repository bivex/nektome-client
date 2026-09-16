using System.Text.Json;
using NektoMe.Application.Abstractions;
using NektoMe.Application.Events;
using NektoMe.Application.Protocol;
using NektoMe.Domain;

namespace NektoMe.Application.Services;

/// <summary>
/// Application-layer use cases for the anonymous chat: connection lifecycle,
/// search, messaging, typing and read receipts. Consumes raw transport
/// notices and translates them into domain state and published events.
/// </summary>
public sealed class ChatSession : IDisposable
{
    private readonly IChatTransport _transport;
    private readonly IAuthKeyService _authKeys;
    private readonly ITokenStore _tokens;
    private readonly IDeviceIdentityProvider _device;
    private readonly IRandomIdGenerator _randomIds;
    private readonly ChatOptions _options;

    /// <summary>randomIds of messages sent by this client that the server has not echoed yet.</summary>
    private readonly HashSet<long> _pendingRandomIds = [];

    private Dialog? _dialog;
    private bool _disposed;

    public ChatSession(
        IChatTransport transport,
        IAuthKeyService authKeys,
        ITokenStore tokens,
        IDeviceIdentityProvider device,
        IRandomIdGenerator randomIds,
        ChatOptions options)
    {
        _transport = transport;
        _authKeys = authKeys;
        _tokens = tokens;
        _device = device;
        _randomIds = randomIds;
        _options = options;

        _transport.Connected += OnTransportConnected;
        _transport.Disconnected += reason => Publish(new ConnectionChanged(false, reason));
        _transport.Reconnecting += () => Publish(new ReconnectingStarted());
        _transport.TransportError += message => Publish(new TransportFailed(message));
        _transport.NoticeReceived += HandleNotice;
    }

    /// <summary>Subscribe here for session events. Handlers may be called from transport threads.</summary>
    public ChatEventStream Events { get; } = new();

    public bool IsConnected => _transport.IsConnected;

    /// <summary>The active dialog, if one is open.</summary>
    public Dialog? CurrentDialog => _dialog;

    /// <summary>User id assigned by the server at authentication time.</summary>
    public long? OwnUserId { get; private set; }

    // -- use cases -----------------------------------------------------------

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        _transport.ConnectAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        _transport.DisconnectAsync(cancellationToken);

    public Task RunSearchAsync(SearchCriteria? criteria = null, CancellationToken cancellationToken = default) =>
        _transport.SendAsync(ProtocolCodec.SearchRun(criteria ?? new SearchCriteria()), cancellationToken);

    public Task StopSearchAsync(CancellationToken cancellationToken = default) =>
        _transport.SendAsync(ProtocolCodec.SearchOut(), cancellationToken);

    /// <summary>
    /// Sends a text message into the open dialog. Returns the generated
    /// correlation id; the echo of this id identifies the server's confirmation.
    /// </summary>
    public async Task<long> SendMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        var dialog = _dialog ?? throw new InvalidOperationException("No open dialog.");
        long randomId = _randomIds.Next();
        _pendingRandomIds.Add(randomId);
        try
        {
            await _transport.SendAsync(ProtocolCodec.AnonMessage(dialog.Id.Value, text, randomId), cancellationToken);
        }
        catch
        {
            _pendingRandomIds.Remove(randomId);
            throw;
        }

        return randomId;
    }

    public Task SetTypingAsync(bool typing, bool voice = false, CancellationToken cancellationToken = default)
    {
        var dialog = _dialog ?? throw new InvalidOperationException("No open dialog.");
        return _transport.SendAsync(ProtocolCodec.DialogTyping(dialog.Id.Value, typing, voice), cancellationToken);
    }

    public Task LeaveDialogAsync(CancellationToken cancellationToken = default)
    {
        var dialog = _dialog ?? throw new InvalidOperationException("No open dialog.");
        return _transport.SendAsync(ProtocolCodec.AnonLeaveDialog(dialog.Id.Value), cancellationToken);
    }

    /// <summary>Reports the highest known message id as read to the interlocutor.</summary>
    public Task MarkReadAsync(CancellationToken cancellationToken = default)
    {
        var dialog = _dialog;
        if (dialog is null || dialog.Messages.Count == 0)
        {
            return Task.CompletedTask;
        }

        long lastId = dialog.Messages.Max(m => m.Id.Value);
        return _transport.SendAsync(ProtocolCodec.AnonReadMessages(dialog.Id.Value, lastId), cancellationToken);
    }

    public Task RefreshDialogInfoAsync(CancellationToken cancellationToken = default)
    {
        var dialog = _dialog ?? throw new InvalidOperationException("No open dialog.");
        return _transport.SendAsync(ProtocolCodec.DialogInfo(dialog.Id.Value), cancellationToken);
    }

    public Task VerifyCaptchaAsync(string solution, CancellationToken cancellationToken = default) =>
        _transport.SendAsync(ProtocolCodec.CaptchaVerify(solution), cancellationToken);

    public Task ReportAsync(long messageId, int reasonId, CancellationToken cancellationToken = default)
    {
        var dialog = _dialog ?? throw new InvalidOperationException("No open dialog.");
        return _transport.SendAsync(ProtocolCodec.ReportDialog(dialog.Id.Value, messageId, reasonId), cancellationToken);
    }

    /// <summary>Forgets the stored token; the next connect performs a fresh getToken.</summary>
    public void ForgetToken() => _tokens.Clear();

    // -- transport wiring ----------------------------------------------------

    private void OnTransportConnected()
    {
        Publish(new ConnectionChanged(true, null));
        _ = SendAuthenticationAsync();
    }

    private async Task SendAuthenticationAsync()
    {
        try
        {
            var device = _device.Get();
            string key = _authKeys.BuildKey(device.DeviceId);
            string? token = _tokens.Get();

            OutboundMessage auth = token is null
                ? ProtocolCodec.AuthGetToken(device, key, _options)
                : ProtocolCodec.AuthSendToken(token, key, _options);

            await _transport.SendAsync(auth);
        }
        catch (Exception ex)
        {
            Publish(new TransportFailed("Authentication failed: " + ex.Message));
        }
    }

    // -- notice handling -----------------------------------------------------

    private void HandleNotice(NoticeMessage notice)
    {
        try
        {
            Dispatch(notice);
        }
        catch (Exception ex)
        {
            Publish(new TransportFailed($"Failed to handle notice '{notice.Name}': {ex.Message}"));
        }
    }

    private void Dispatch(NoticeMessage notice)
    {
        switch (notice.Name)
        {
            case WireNames.AuthSuccessToken:
                HandleAuthSuccess(notice.Data, persistToken: true);
                break;

            case WireNames.AuthSuccessUser:
                HandleAuthSuccess(notice.Data, persistToken: false);
                break;

            case WireNames.ErrorCode:
                HandleError(notice.Data);
                break;

            case WireNames.DialogOpened:
                HandleDialogOpened(notice.Data);
                break;

            case WireNames.DialogClosed:
                HandleDialogClosed(notice.Data);
                break;

            case WireNames.NewMessage:
                HandleNewMessage(notice.Data);
                break;

            case WireNames.ReadMessages:
                HandleMessagesRead(notice.Data);
                break;

            case WireNames.DialogTypingNotice:
                HandleTyping(notice.Data);
                break;

            case WireNames.SearchSuccess:
                Publish(new SearchStateChanged(true));
                break;

            case WireNames.SearchOutNotice:
                Publish(new SearchStateChanged(false));
                break;

            case WireNames.OnlineCount:
                HandleOnlineCount(notice.Data);
                break;

            case WireNames.CaptchaVerifyNotice:
                HandleCaptcha(notice.Data);
                break;

            case WireNames.DialogPaid:
                HandleDialogPaid(notice.Data);
                break;

            case WireNames.PurchaseChanged:
                var purchase = notice.Data is { } pd ? ProtocolCodec.Parse<PurchaseData>(pd) : null;
                Publish(new PurchaseChanged(purchase?.PaidType));
                break;

            case WireNames.DialogInfoNotice:
                HandleDialogOpened(notice.Data);
                break;

            case WireNames.SocketClose:
                Publish(new ServerRequestedClose(null));
                break;

            default:
                Publish(new RawNotice(notice.Name, notice.Badge, notice.Page));
                break;
        }
    }

    private void HandleAuthSuccess(JsonElement? data, bool persistToken)
    {
        var auth = data is { } d ? ProtocolCodec.Parse<AuthTokenData>(d) : null;

        string? issued = auth?.TokenInfo?.AuthToken;
        if (persistToken && !string.IsNullOrEmpty(issued))
        {
            _tokens.Set(issued);
        }

        OwnUserId = auth?.Id;

        var ban = auth?.StatusInfo is { } status
            ? new BanStatus(status.CommunicationBan ?? false, status.AnonDialogId)
            : BanStatus.None;

        Publish(new Authenticated(auth?.Id, ban, auth?.Config is { } config ? MapConfig(config) : null));
    }

    private void HandleError(JsonElement? data)
    {
        var error = data is { } d ? ProtocolCodec.Parse<ErrorData>(d) : null;
        IReadOnlyDictionary<string, string>? additional = null;
        if (error?.Additional is { } raw)
        {
            additional = raw.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        }

        Publish(new ErrorReceived(new ChatError(error?.Code, error?.Description ?? "Unknown error", additional)));
    }

    private void HandleDialogOpened(JsonElement? data)
    {
        if (data is not { } d)
        {
            return;
        }

        var dialogData = ProtocolCodec.Parse<DialogData>(d);
        if (dialogData.Id is not { } id)
        {
            return;
        }

        var interlocutors = (dialogData.Interlocutors ?? []).Select(v => new UserId(v)).ToList();
        var dialog = new Dialog(
            new DialogId(id),
            interlocutors,
            dialogData.SupportVoice ?? false,
            ProtocolCodec.FromUnix(dialogData.CreateTime),
            ProtocolCodec.FromUnix(dialogData.UpdateTime));

        foreach (var messageData in dialogData.Messages ?? [])
        {
            if (MapMessage(messageData) is { } message)
            {
                dialog.TryAddMessage(message);
            }
        }

        if (dialogData.Close == true)
        {
            dialog.Close();
        }

        _dialog = dialog;
        Publish(new DialogOpened(dialog));
    }

    private void HandleDialogClosed(JsonElement? data)
    {
        long dialogId = data is { } d && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var parsed)
            ? parsed
            : _dialog?.Id.Value ?? 0;

        _dialog?.Close();
        Publish(new DialogClosed(new DialogId(dialogId)));
    }

    private void HandleNewMessage(JsonElement? data)
    {
        if (data is not { } d)
        {
            return;
        }

        var messageData = ProtocolCodec.Parse<MessageData>(d);
        if (MapMessage(messageData) is not { } message)
        {
            return;
        }

        bool ownMessage = messageData.RandomId is { } randomId && _pendingRandomIds.Remove(randomId);
        if (!ownMessage && OwnUserId is { } own && message.SenderId.Value == own)
        {
            ownMessage = true;
        }

        if (_dialog is null && message.DialogId.Value != 0)
        {
            _dialog = new Dialog(message.DialogId, []);
        }

        _dialog?.TryAddMessage(message);
        Publish(new MessageReceived(message, ownMessage));
    }

    private void HandleMessagesRead(JsonElement? data)
    {
        if (data is not { } d)
        {
            return;
        }

        var readData = ProtocolCodec.Parse<MessagesReadData>(d);
        if (readData.DialogId is not { } dialogId)
        {
            return;
        }

        List<long> ids = readData.Reads ?? [];
        _dialog?.MarkRead(new MessageId(ids.Count > 0 ? ids.Max() : 0));
        Publish(new MessagesRead(new DialogId(dialogId), ids));
    }

    private void HandleTyping(JsonElement? data)
    {
        if (data is not { } d)
        {
            return;
        }

        var typing = ProtocolCodec.Parse<TypingData>(d);
        if (typing.DialogId is { } dialogId)
        {
            Publish(new TypingChanged(new DialogId(dialogId), typing.Typing ?? false, typing.Voice ?? false));
        }
    }

    private void HandleOnlineCount(JsonElement? data)
    {
        if (data is not { } d)
        {
            return;
        }

        var online = ProtocolCodec.Parse<OnlineCountData>(d);
        Publish(new OnlineCountReceived(online.InChats ?? 0, online.InSearch ?? 0));
    }

    private void HandleCaptcha(JsonElement? data)
    {
        string? solution = data is { } d ? ProtocolCodec.Parse<CaptchaData>(d).Solution : null;
        Publish(new CaptchaRequired(solution));
    }

    private void HandleDialogPaid(JsonElement? data)
    {
        if (data is not { } d)
        {
            return;
        }

        var paid = ProtocolCodec.Parse<DialogPaidData>(d);
        if (paid.DialogId is { } dialogId)
        {
            Publish(new DialogPaid(new DialogId(dialogId), paid.Paid ?? false));
        }
    }

    // -- mapping helpers -----------------------------------------------------

    private static Message? MapMessage(MessageData data)
    {
        if (data.Id is not { } id || string.IsNullOrEmpty(data.Text))
        {
            return null;
        }

        return new Message(
            new MessageId(id),
            new DialogId(data.DialogId ?? 0),
            new UserId(data.SenderId ?? 0),
            data.Text,
            ProtocolCodec.FromUnix(data.CreateTime),
            data.RandomId ?? 0,
            data.Read ?? false);
    }

    private static AppConfig MapConfig(AuthConfigData config)
    {
        List<AgeGroup> Map(List<AgeGroupData>? source) =>
            source?.Select(a => new AgeGroup(a.Name ?? string.Empty, a.To, a.Selected ?? false)).ToList()
            ?? [];

        return new AppConfig(
            config.AlertInfo,
            config.BanTalk ?? false,
            config.RulesEnable ?? false,
            config.AudioChat ?? false,
            config.SubsDisabled ?? false,
            config.ReportReasons ?? [],
            Map(config.Ages?.Adult),
            Map(config.Ages?.Communication),
            Map(config.Ages?.Role),
            config.VoiceConfig is { } voice
                ? new VoiceConfig(
                    voice.AvailableSeconds ?? 0,
                    voice.MaxSeconds ?? 0,
                    voice.MinSeconds ?? 0,
                    voice.Beta ?? false)
                : null);
    }

    private void Publish(ChatEvent e) => Events.Publish(e);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.Dispose();
    }
}
