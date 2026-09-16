namespace NektoMe.Ui.ViewModels;

/// <summary>One rendered chat message.</summary>
public sealed record ChatLine(DateTimeOffset Time, string Sender, string Text, bool IsOwn)
{
    public string DisplayTime => Time == default ? string.Empty : Time.ToLocalTime().ToString("HH:mm");
}
