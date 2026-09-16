using NektoMe.Application.Events;
using NektoMe.Application.Protocol;
using NektoMe.Domain;

namespace NektoMe.Application.Tests;

public class ChatSessionTests
{
    [Fact]
    public async Task Connect_WithoutToken_SendsGetToken()
    {
        var (session, transport, tokens) = TestSession.Create();

        await session.ConnectAsync();

        Assert.True(transport.IsConnected);
        Assert.Contains("auth.getToken", transport.LastSentJson);
        Assert.Contains("device-1", transport.LastSentJson);
        Assert.Null(tokens.Get());
    }

    [Fact]
    public async Task Connect_WithStoredToken_SendsSendToken()
    {
        var (session, transport, tokens) = TestSession.Create();
        tokens.Set("stored-token");

        await session.ConnectAsync();

        Assert.Contains("auth.sendToken", transport.LastSentJson);
        Assert.Contains("stored-token", transport.LastSentJson);
    }

    [Fact]
    public async Task SuccessToken_PersistsTokenAndPublishesAuthenticated()
    {
        var (session, transport, tokens) = TestSession.Create();
        var events = new List<ChatEvent>();
        using var sub = session.Events.Subscribe(events.Add);

        transport.SimulateNotice(WireNames.AuthSuccessToken, """
        {"id":42,"tokenInfo":{"authToken":"T-1","paidType":0},"statusInfo":{"communicationBan":true,"anonDialogId":7}}
        """);

        Assert.Equal("T-1", tokens.Get());
        var auth = events.OfType<Authenticated>().Single();
        Assert.Equal(42, auth.UserId);
        Assert.True(auth.Ban.CommunicationBan);
        Assert.Equal(7, auth.Ban.AnonDialogId);
    }

    [Fact]
    public void DialogOpened_LoadsHistoryIntoDomain()
    {
        var (session, transport, _) = TestSession.Create();
        transport.SimulateNotice(WireNames.DialogOpened, """
        {"id":7,"interlocutors":[5],"supportVoice":true,"createTime":1700000000,"messages":[
            {"id":3,"dialogId":7,"senderId":5,"message":"hi","createTime":1700000000,"read":false}
        ]}
        """);

        var dialog = session.CurrentDialog;
        Assert.NotNull(dialog);
        Assert.Equal(7, dialog.Id.Value);
        Assert.True(dialog.SupportsVoice);
        Assert.Single(dialog.Messages);
        Assert.Equal("hi", dialog.Messages[0].Text);
        Assert.Equal(new UserId(5), dialog.Messages[0].SenderId);
    }

    [Fact]
    public async Task NewMessage_IsAddedAndPublished_WithOwnDetectionByRandomId()
    {
        var (session, transport, _) = TestSession.Create();
        transport.SimulateNotice(WireNames.DialogOpened, """{"id":7,"interlocutors":[5]}""");
        var events = new List<ChatEvent>();
        using var sub = session.Events.Subscribe(events.Add);

        await session.SendMessageAsync("hello");

        Assert.Contains("anon.message", transport.LastSentJson);
        Assert.Contains("hello", transport.LastSentJson);
        Assert.Contains("1001", transport.LastSentJson);

        // Echo with our randomId => own message.
        transport.SimulateNotice(WireNames.NewMessage, """
        {"id":11,"dialogId":7,"senderId":999,"message":"hello","randomId":1001,"createTime":1700000005}
        """);
        var own = events.OfType<MessageReceived>().Single();
        Assert.True(own.OwnMessage);
        Assert.Equal(11, own.Message.Id.Value);

        // Incoming from partner.
        transport.SimulateNotice(WireNames.NewMessage, """
        {"id":12,"dialogId":7,"senderId":5,"message":"hey","createTime":1700000006}
        """);
        var theirs = events.OfType<MessageReceived>().Last();
        Assert.False(theirs.OwnMessage);
        Assert.Equal(2, session.CurrentDialog!.Messages.Count);
    }

    [Fact]
    public async Task MarkRead_SendsHighestMessageId()
    {
        var (session, transport, _) = TestSession.Create();
        transport.SimulateNotice(WireNames.DialogOpened, """
        {"id":7,"interlocutors":[5],"messages":[
            {"id":3,"dialogId":7,"senderId":5,"message":"a","createTime":1},
            {"id":9,"dialogId":7,"senderId":5,"message":"b","createTime":2}
        ]}
        """);

        await session.MarkReadAsync();

        Assert.Contains("anon.readMessages", transport.LastSentJson);
        Assert.Contains("\"lastMessageId\":9", transport.LastSentJson);
    }

    [Fact]
    public void ErrorNotice_IsPublishedAsChatError()
    {
        var (session, transport, _) = TestSession.Create();
        var events = new List<ChatEvent>();
        using var sub = session.Events.Subscribe(events.Add);

        transport.SimulateNotice(WireNames.ErrorCode, """{"code":5,"description":"banned"}""");

        var error = events.OfType<ErrorReceived>().Single();
        Assert.Equal(5, error.Error.Code);
        Assert.Equal("banned", error.Error.Description);
    }

    [Fact]
    public void SearchNotices_ToggleState()
    {
        var (session, transport, _) = TestSession.Create();
        var events = new List<ChatEvent>();
        using var sub = session.Events.Subscribe(events.Add);

        transport.SimulateNotice(WireNames.SearchSuccess);
        transport.SimulateNotice(WireNames.SearchOutNotice);

        Assert.True(events.OfType<SearchStateChanged>().First().Searching);
        Assert.False(events.OfType<SearchStateChanged>().Last().Searching);
    }

    [Fact]
    public void TypingNotice_IsPublished()
    {
        var (session, transport, _) = TestSession.Create();
        var events = new List<ChatEvent>();
        using var sub = session.Events.Subscribe(events.Add);

        transport.SimulateNotice(WireNames.DialogTypingNotice, """{"dialogId":7,"typing":true}""");

        var typing = events.OfType<TypingChanged>().Single();
        Assert.Equal(7, typing.DialogId.Value);
        Assert.True(typing.Typing);
    }

    [Fact]
    public void UnknownNotice_FallsBackToRaw()
    {
        var (session, transport, _) = TestSession.Create();
        var events = new List<ChatEvent>();
        using var sub = session.Events.Subscribe(events.Add);

        transport.SimulateNotice("something.unexpected");

        Assert.Contains(events, e => e is RawNotice { Name: "something.unexpected" });
    }

    [Fact]
    public async Task DialogClosed_PublishesAndClosesDomainDialog()
    {
        var (session, transport, _) = TestSession.Create();
        transport.SimulateNotice(WireNames.DialogOpened, """{"id":7,"interlocutors":[5]}""");
        var events = new List<ChatEvent>();
        using var sub = session.Events.Subscribe(events.Add);

        transport.SimulateNotice(WireNames.DialogClosed, "7");

        Assert.Equal(DialogStatus.Closed, session.CurrentDialog!.Status);
        Assert.Equal(7, events.OfType<DialogClosed>().Single().DialogId.Value);
    }
}
