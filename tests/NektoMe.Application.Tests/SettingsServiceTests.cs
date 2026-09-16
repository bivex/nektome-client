using NektoMe.Application.Services;

namespace NektoMe.Application.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void Normalize_restores_default_when_gain_below_slider_floor()
    {
        // Old builds allowed 0% via the slider; 0 multiplies every mic sample
        // by zero and sends digital silence to the peer. Clamping it to the
        // 25% floor still sends a near-silent signal, so it resets to default.
        var settings = new AppSettings { MicGainPercent = 0 };

        settings.Normalize();

        Assert.Equal(AppSettings.MicGainDefaultPercent, settings.MicGainPercent);
    }

    [Fact]
    public void Normalize_keeps_in_range_gain_unchanged()
    {
        var settings = new AppSettings { MicGainPercent = 200 };

        settings.Normalize();

        Assert.Equal(200, settings.MicGainPercent);
    }

    [Fact]
    public void Normalize_restores_default_when_gain_above_slider_ceiling()
    {
        var settings = new AppSettings { MicGainPercent = 900 };

        settings.Normalize();

        Assert.Equal(AppSettings.MicGainDefaultPercent, settings.MicGainPercent);
    }
}
