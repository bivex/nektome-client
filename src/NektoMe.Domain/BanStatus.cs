namespace NektoMe.Domain;

/// <summary>Account standing reported at authentication time.</summary>
public sealed record BanStatus(bool CommunicationBan, long? AnonDialogId)
{
    public static readonly BanStatus None = new(false, null);
}
