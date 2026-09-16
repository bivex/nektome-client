namespace NektoMe.Application.Abstractions;

/// <summary>Persists the server-issued auth token between sessions.</summary>
public interface ITokenStore
{
    string? Get();

    void Set(string token);

    void Clear();
}
