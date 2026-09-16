namespace NektoMe.Domain;

/// <summary>Server-reported error ("error.code" notice).</summary>
public sealed record ChatError(
    int? Code,
    string Description,
    IReadOnlyDictionary<string, string>? Additional = null)
{
    public static readonly ChatError Unknown = new(null, "Unknown error", null);
}
