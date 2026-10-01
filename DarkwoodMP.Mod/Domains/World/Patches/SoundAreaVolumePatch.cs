using DWMPHorde.Audio;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;
using DWMPHorde.Harmony;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// On client, adds the SoundArea volume as heard from the local listen position
    /// (spectator-aware) so ambient loops stay audible when the listener is not the body.
    /// Vanilla's rule is kept: an onlyOneInstance AudioObject is shared by every SoundArea that
    /// plays it and the LOUDEST candidate of the frame wins (thisFrameVolume is reset in
    /// LateUpdate). Same formula as vanilla Update, so with the listener on the body this is a no-op.
    /// </summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(SoundArea), "Update")]
    public static class SoundAreaUpdatePatch
    {
        private static readonly AccessTools.FieldRef<SoundArea, float> ItemVolume =
            AccessTools.FieldRefAccess<SoundArea, float>("itemVolume");

        static void Postfix(SoundArea __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected
                || ModRuntime.Network.Role != NetworkRole.Client)
                return;
            if (__instance.soundAO == null) return;
            if (!__instance.onlyOneInstance) return;
            // Vanilla Update returns early without a source.
            if (!__instance.hasSource || __instance.source == null) return;

            Vector3 listen = LocalAudioService.GetListenPosition();
            float dist = Core.trueDistance(listen, __instance.source.position);
            float minDist = __instance.minSourceDistance;
            float maxDist = __instance.maxSourceDistance;

            float itemVolume = ItemVolume(__instance);
            float full = itemVolume * __instance.volumeModifier;
            float candidate = dist < minDist
                ? full
                : Mathf.Clamp(itemVolume * (maxDist - dist) / (maxDist - minDist) * __instance.volumeModifier, 0.001f, 1f);

            // Loudest wins, exactly like vanilla's per-frame arbitration.
            if (candidate > __instance.soundAO.thisFrameVolume)
            {
                __instance.soundAO.volume = candidate;
                __instance.soundAO.thisFrameVolume = candidate;
            }
        }
    }

    /// <summary>Resets SoundArea.thisFrameVolume so the next Update recalculates.</summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(SoundArea), "LateUpdate")]
    public static class SoundAreaLateUpdatePatch
    {
        static void Postfix(SoundArea __instance)
        {
            if (!__instance.onlyOneInstance) return;
            if (__instance.soundAO == null) return;
            __instance.soundAO.thisFrameVolume = 0f;
        }
    }
}
