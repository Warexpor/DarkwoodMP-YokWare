using DWMPHorde.Audio;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>The voice volume sliders: left end muted, middle as recorded, right end +24 dB, whole dB steps.</summary>
public class VolumeScaleTests
{
    [Fact]
    public void Ends_and_middle()
    {
        Assert.Equal(0f, VolumeScale.FromSlider(0f));
        Assert.Equal(1f, VolumeScale.FromSlider(0.5f));
        Assert.InRange(VolumeScale.FromSlider(1f), 15.8f, 15.9f);
        Assert.Equal(VolumeScale.MaxGain, VolumeScale.FromSlider(1f), 4);
    }

    [Fact]
    public void Round_trips_in_whole_dB()
    {
        for (int db = -23; db <= 24; db++)
        {
            float t = (db - VolumeScale.MinDb) / (VolumeScale.MaxDb - VolumeScale.MinDb);
            float g = VolumeScale.FromSlider(t);
            Assert.Equal(t, VolumeScale.ToSlider(g), 4);
        }
    }

    [Fact]
    public void Old_settings_stay_where_they_were()
    {
        // 2 was the old right end: +6 dB, a quarter of the way past the middle.
        Assert.Equal(0.5f + 6.0206f / 48f, VolumeScale.ToSlider(2f), 3);
        Assert.Equal(0f, VolumeScale.ToSlider(0f));
        Assert.Equal(1f, VolumeScale.ToSlider(100f));
        Assert.Equal(VolumeScale.MaxGain, VolumeScale.Clamp(100f));
    }
}
