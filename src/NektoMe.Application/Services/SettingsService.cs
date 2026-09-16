using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NektoMe.Application.Services;

public class AppSettings
{
    // Proxy-Seller Settings
    public string ProxySellerApiKey { get; set; } = string.Empty;
    public string ProxySellerListName { get; set; } = "nektome_app";
    public int ProxySellerPortsCount { get; set; } = 20;
    public int ProxySellerRotationSeconds { get; set; } = 3600;
    public string ProxySellerWhitelistIp { get; set; } = string.Empty;

    // DSP Settings
    public float NoiseGateThreshold { get; set; } = 0.005f;
    public float CompressorThreshold { get; set; } = 0.3f;
    public float CompressorRatio { get; set; } = 4.0f;
    public float MakeupGain { get; set; } = 2.0f;

    public string SavedProxies { get; set; } = string.Empty;

    // App UI State
    public string WebToken { get; set; } = string.Empty;
    public string AndroidId { get; set; } = string.Empty;
    public double SpeakerGainPercent { get; set; } = 100;
    public double MicGainPercent { get; set; } = MicGainDefaultPercent;
    public bool MicAgcEnabled { get; set; } = true;
    public bool MicDspEnabled { get; set; } = true;
    public bool SpeakerDspEnabled { get; set; } = true;
    public bool EchoCancellationEnabled { get; set; } = true;

    // The mic slider has no 0% stop: at 0% every mic sample is multiplied by
    // zero and digital silence goes to the peer ("they can't hear me").
    public const double MicGainMinPercent = 25;
    public const double MicGainMaxPercent = 400;
    public const double MicGainDefaultPercent = 200;

    /// <summary>Fixes values from configs written by older builds. Out-of-range
    /// is treated as corrupt and reset to the default: clamping a legacy 0% to
    /// the 25% floor still sends a near-silent signal to the peer.</summary>
    public void Normalize()
    {
        if (MicGainPercent < MicGainMinPercent || MicGainPercent > MicGainMaxPercent)
        {
            MicGainPercent = MicGainDefaultPercent;
        }
    }
}

public static class SettingsService
{
    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
            catch
            {
                settings = new AppSettings();
            }
        }

        settings.Normalize();
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(settings, Options));
        }
        catch { }
    }
}
