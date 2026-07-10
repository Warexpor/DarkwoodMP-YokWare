using DWMPHorde.Audio;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// On client, re-evaluates SoundArea volume from the local listen position
    /// (spectator-aware) so ambient loops stay audible when the listener moves.
    /// </summary>
    [HarmonyPatch(typeof(SoundArea), "Update")]
    public static class SoundAreaUpdatePatch
    {
        static void Postfix(SoundArea __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Client)
                return;
            if (__instance.soundAO == null) return;
            if (!__instance.onlyOneInstance) return;

            Transform src = __instance.source;
            if (src == null) src = __instance.transform;

            Vector3 listen = LocalAudioService.GetListenPosition();
            float dist = Vector3.Distance(listen, src.position);
            float maxDist = __instance.maxSourceDistance > 0f
                ? __instance.maxSourceDistance
                : LocalAudioService.DefaultMaxAudioDistance;
            float minDist = __instance.minSourceDistance;

            float targetVol;
            if (dist > maxDist)
                targetVol = 0.001f;
            else if (dist < minDist)
                targetVol = __instance.volumeModifier;
            else
            {
                float t = (dist - minDist) / (maxDist - minDist);
                targetVol = Mathf.Max(Mathf.Lerp(__instance.volumeModifier, 0.001f, t), 0.001f);
            }

            float currentVol = __instance.soundAO.volume;
            if (Mathf.Abs(currentVol - targetVol) > 0.001f)
            {
                __instance.soundAO.volume = targetVol;
                __instance.soundAO.thisFrameVolume = targetVol;
            }
        }
    }

    /// <summary>Resets SoundArea.thisFrameVolume so the next Update recalculates.</summary>
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
