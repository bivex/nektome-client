using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NektoMe.Ui.Services;

public static class ChromeTokenExtractor
{
    private static readonly Regex TokenRegex = new(
        @"\""authToken\""\s*:\s*\""([a-f0-9\-]{36})\""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string? TryExtractWebToken()
    {
        try
        {
            string? levelDbDir = GetChromeLevelDbPath();
            if (levelDbDir is null || !Directory.Exists(levelDbDir))
            {
                return null;
            }

            string[] files = Directory.GetFiles(levelDbDir, "*.ldb");
            string[] logs = Directory.GetFiles(levelDbDir, "*.log");

            // Check newest files first
            var allFiles = new string[files.Length + logs.Length];
            Array.Copy(files, 0, allFiles, 0, files.Length);
            Array.Copy(logs, 0, allFiles, files.Length, logs.Length);
            Array.Sort(allFiles, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));

            foreach (string file in allFiles)
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);
                    string content = reader.ReadToEnd();

                    int idx = content.IndexOf("storage_audio_v2", StringComparison.Ordinal);
                    if (idx != -1)
                    {
                        int searchLength = Math.Min(2000, content.Length - idx);
                        string chunk = content.Substring(idx, searchLength);
                        Match match = TokenRegex.Match(chunk);
                        if (match.Success)
                        {
                            return match.Groups[1].Value;
                        }
                    }
                }
                catch
                {
                    // File might be locked by running Chrome
                }
            }
        }
        catch
        {
            // Ignore filesystem errors
        }

        return null;
    }

    private static string? GetChromeLevelDbPath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "Google", "Chrome", "Default", "Local Storage", "leveldb");
        }
        if (OperatingSystem.IsWindows())
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default", "Local Storage", "leveldb");
        }
        if (OperatingSystem.IsLinux())
        {
            return Path.Combine(home, ".config", "google-chrome", "Default", "Local Storage", "leveldb");
        }
        return null;
    }
}
