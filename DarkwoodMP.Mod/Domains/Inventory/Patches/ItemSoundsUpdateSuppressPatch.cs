using DWMPHorde.Audio;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// When MOS owns the scrape for a networked object, skip native
    /// <see cref="ItemSounds.Update"/> moving-loop logic entirely.
    /// ItemSounds.Update only drives the moving scrape (start/stop movingSoundAO),
    /// so returning false is safe and prevents double-scrape on remote peers.
    /// </summary>
    [HarmonyPatch(typeof(ItemSounds), "Update")]
    public static class ItemSoundsUpdateSuppressPatch
    {
        private static bool Prefix(ItemSounds __instance)
        {
            if (__instance == null || __instance.gameObject == null)
                return true;

            // Single-player / not connected: never suppress.
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected)
                return true;

            string name = __instance.gameObject.name;
            if (!ItemMovingSoundHelper.IsRemoteScrape(name))
                return true;

            // Remote-owned: ensure any already-armed native AO is killed once.
            try
            {
                var ao = Traverse.Create(__instance).Field("movingSoundAO").GetValue<AudioObject>();
                if (ao != null)
                {
                    ao.Stop(ItemMovingSoundHelper.IntentionalStopFade);
                    Traverse.Create(__instance).Field("movingSoundAO").SetValue(null);
                }
            }
            catch
            {
                // ignore traverse failure — MOS path still owns playback
            }

            return false;
        }
    }
}
