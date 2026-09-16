using NektoMe.Application.Abstractions;

namespace NektoMe.Infrastructure.Persistence;

/// <summary>Plain-text token persistence under ~/.nektome-client/.</summary>
public sealed class FileTokenStore : ITokenStore
{
    private readonly string _path;

    public FileTokenStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? DefaultDirectory, "token.txt");
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".nektome-client");

    public string? Get() => File.Exists(_path) ? File.ReadAllText(_path).Trim() is { Length: > 0 } token ? token : null : null;

    public void Set(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, token);
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
