namespace NektoMe.Domain;

public enum Sex
{
    Female,
    Male,
}

public sealed record AgeGroup(string Name, int? To, bool Selected);

public sealed record VoiceConfig(int AvailableSeconds, int MaxSeconds, int MinSeconds, bool Beta);

/// <summary>Feature flags and search-rule configuration pushed by the server at auth time.</summary>
public sealed record AppConfig(
    string? AlertInfo,
    bool BanTalk,
    bool RulesEnabled,
    bool AudioChatEnabled,
    bool SubscriptionsDisabled,
    IReadOnlyList<string> ReportReasons,
    IReadOnlyList<AgeGroup> AdultAges,
    IReadOnlyList<AgeGroup> CommunicationAges,
    IReadOnlyList<AgeGroup> RoleAges,
    VoiceConfig? Voice);
