using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.Patches;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Applies rain/fog/lightning state broadcast by the time authority
/// (Weather_Patch). Ported from the BepInEx mod's WeatherSync. Reflection is
/// used only for the private "raining" backing field; everything else is
/// public on the Rain singleton.
/// </summary>
public class WeatherSync
{
    private readonly NetworkLayer _network;
    private static FieldInfo _rainingField;

    public WeatherSync(NetworkLayer network)
    {
        _network = network;
    }

    /// <summary>Build the "weather:..." wire string from the current Rain state.</summary>
    public static string Encode(Rain rain)
    {
        var inv = CultureInfo.InvariantCulture;
        int fadeInTime = 0, fadeInDay = 0, fadeOutTime = 0, fadeOutDay = 0;
        if (rain.timeToFadeInFog != null) { fadeInTime = rain.timeToFadeInFog.time; fadeInDay = rain.timeToFadeInFog.day; }
        if (rain.timeToFadeOutFog != null) { fadeOutTime = rain.timeToFadeOutFog.time; fadeOutDay = rain.timeToFadeOutFog.day; }

        return "weather:"
            + (rain.Raining ? 1 : 0) + ":"
            + (rain.rainToday ? 1 : 0) + ":"
            + rain.timeToStart.ToString("F2", inv) + ":"
            + rain.lightningTime.ToString("F2", inv) + ":"
            + (rain.preRainLightning ? 1 : 0) + ":"
            + rain.preRainLightningTime.ToString("F2", inv) + ":"
            + rain.duration.ToString("F2", inv) + ":"
            + (rain.fogFadedOutToday ? 1 : 0) + ":"
            + (rain.fogIsActive ? 1 : 0) + ":"
            + fadeInTime + ":" + fadeInDay + ":"
            + fadeOutTime + ":" + fadeOutDay;
    }

    /// <summary>Receive side: apply a "weather:..." payload (already stripped of the channel).</summary>
    public void OnRemoteWeather(string payload)
    {
        var rain = UnityEngine.Object.FindObjectOfType<Rain>();
        if (rain == null) return;

        var p = payload.Split(':');
        if (p.Length != 13) return;
        var inv = CultureInfo.InvariantCulture;

        try
        {
            var raining = p[0] == "1";
            var fogActive = p[8] == "1";
            var wasRaining = rain.Raining;
            var wasFog = rain.fogIsActive;

            RemoteApply.Active = true;
            try
            {
                SetRainingField(rain, raining);
                rain.rainToday = p[1] == "1";
                if (float.TryParse(p[2], NumberStyles.Float, inv, out var f)) rain.timeToStart = f;
                if (float.TryParse(p[3], NumberStyles.Float, inv, out f)) rain.lightningTime = f;
                rain.preRainLightning = p[4] == "1";
                if (float.TryParse(p[5], NumberStyles.Float, inv, out f)) rain.preRainLightningTime = f;
                if (float.TryParse(p[6], NumberStyles.Float, inv, out f)) rain.duration = f;
                rain.fogFadedOutToday = p[7] == "1";
                rain.fogIsActive = fogActive;

                if (int.TryParse(p[9], out var fit) && int.TryParse(p[10], out var fid))
                    SetFogTime(ref rain.timeToFadeInFog, fit, fid);
                if (int.TryParse(p[11], out var fot) && int.TryParse(p[12], out var fod))
                    SetFogTime(ref rain.timeToFadeOutFog, fot, fod);

                // Trigger the actual visuals only when a state flipped
                if (raining != wasRaining) rain.Raining = raining;
                if (fogActive != wasFog) { if (fogActive) rain.startFog(); else rain.stopFog(); }
            }
            finally
            {
                RemoteApply.Active = false;
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[WeatherSync] apply failed: {ex.Message}");
        }
    }

    /// <summary>Authority: add current weather to the join snapshot so late joiners converge.</summary>
    public void CollectSnapshot(List<Packet> into)
    {
        var rain = UnityEngine.Object.FindObjectOfType<Rain>();
        if (rain == null) return;
        var payload = Encode(rain);
        if (payload.StartsWith("weather:", StringComparison.Ordinal))
            payload = payload.Substring("weather:".Length);
        into.Add(new WeatherStatePacket
        {
            PlayerId = Math.Max(_network.LocalClientId, 0),
            Payload = payload
        });
    }

    private static void SetFogTime(ref TimeAndDay slot, int time, int day)
    {
        if (slot == null) slot = new TimeAndDay(time, day);
        else { slot.time = time; slot.day = day; }
    }

    private static void SetRainingField(Rain rain, bool value)
    {
        if (_rainingField == null)
            _rainingField = typeof(Rain).GetField("raining",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _rainingField?.SetValue(rain, value);
    }
}
